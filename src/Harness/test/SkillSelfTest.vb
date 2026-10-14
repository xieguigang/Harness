Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Text
Imports Harness.SkillSystem

Namespace HarnessSelfTest

    ''' <summary>
    ''' offline verification of the skill system against the layout the nature skills
    ''' use: a SKILL.md router, an optional manifest.yaml with always_load / axes /
    ''' on_demand tables, sibling shared packages and scripts.
    ''' 
    ''' the fixtures are generated into a temporary folder, so the test is deterministic
    ''' and needs no network; when a real nature-skills checkout is available it is
    ''' additionally verified read only.
    ''' </summary>
    Friend Module SkillSelfTest

        ''' <summary>the checkout that is verified in addition to the fixtures, when present.</summary>
        Private ReadOnly NatureSkillsCandidates As String() = {
            "C:\Users\Administrator\Downloads\nature-skills\skills",
            "C:\Users\Administrator\Downloads\nature-skills"
        }

        Private _passed As Integer
        Private _failed As Integer
        Private ReadOnly _failures As New List(Of String)
        Private _root As String

        Friend Function RunAll() As Integer
            _root = Path.Combine(Path.GetTempPath(), "harness-skill-selftest-" & Guid.NewGuid().ToString("N").Substring(0, 8))

            Try
                BuildFixtures()
                RunAllFixturesTests()
                RunNatureSkillsTests()
            Catch ex As Exception
                Fail("unexpected " & ex.GetType().Name & ": " & ex.Message)
            Finally
                Try
                    Directory.Delete(_root, True)
                Catch
                End Try
            End Try

            Console.WriteLine()
            Console.WriteLine($"[Skill self test] {_passed} passed, {_failed} failed.")

            For Each failure In _failures
                Console.WriteLine("  FAILED: " & failure)
            Next

            Return If(_failed = 0, 0, 1)
        End Function

        Private Sub RunAllFixturesTests()
            Dim skills As String = Path.Combine(_root, "skills")
            Dim manager As New SkillManager(skills)

            ' ---- layer 1: scanning + shared package detection -------------------
            Dim scanned As Integer = manager.ScanSkills()
            Check(scanned = 4, "four fixture skills must be scanned, got " & scanned)

            Dim sharedPkg As SkillMetadata = manager.GetMetadata("nature-shared")
            Check(sharedPkg IsNot Nothing, "the shared package must be scanned")
            Check(sharedPkg IsNot Nothing AndAlso sharedPkg.IsSharedPackage,
                  "an 'internal shared-reference support package' must be flagged as shared")

            Dim routable As IReadOnlyList(Of SkillMetadata) = manager.GetRoutableMetadata()
            Check(routable.Count = 3, "the shared package must not appear in the router catalog, got " & routable.Count)
            Check(routable.All(Function(m) m.Name <> "nature-shared"), "the shared package must not be routable")

            ' ---- layer 1: description handling ---------------------------------
            Dim demo As SkillMetadata = manager.GetMetadata("demo-skill")
            Check(demo IsNot Nothing, "demo-skill must be scanned")
            Check(demo IsNot Nothing AndAlso Not demo.Description.Contains(vbLf) AndAlso Not demo.Description.Contains(vbTab),
                  "a block scalar description must be folded into a single line")
            Check(demo IsNot Nothing AndAlso demo.Description.Contains("planning the release"),
                  "the folded description must keep its content: " & If(demo?.Description, ""))

            Dim summary As String = demo.ToSummaryLine()
            Check(Not summary.Contains(vbLf), "a summary line must never contain a newline")
            Check(summary.Length <= 330, "a summary line must be truncated, got " & summary.Length & " characters")

            Dim broken As SkillMetadata = manager.GetMetadata("long-meta-skill")
            Check(broken IsNot Nothing, "long-meta-skill must be scanned")
            Check(broken IsNot Nothing AndAlso broken.Description.Length > 4000,
                  "a long front matter must not be truncated at the old 4KB budget, got " & If(broken?.Description?.Length, 0) & " characters")
            Check(broken IsNot Nothing AndAlso broken.Description.EndsWith("END-OF-DESCRIPTION"),
                  "the tail of a long description must survive")

            ' ---- layer 2: manifest + body injection ----------------------------
            Dim definition As SkillDefinition = manager.LoadFullSkill("demo-skill")
            Check(definition IsNot Nothing, "demo-skill must load")

            Dim fragment As String = definition.BuildPromptFragment()
            Check(fragment.Contains("## Skill Instructions"),
                  "a skill without the well known headings must inject its body")

            ' a long description is also asserted for its raw form
            Check(definition.Manifest IsNot Nothing AndAlso definition.Manifest.IsLoaded, "the manifest must be loaded")
            Check(definition.Manifest.AlwaysLoad.Count = 3, "always_load must be read, got " & definition.Manifest.AlwaysLoad.Count)
            Check(definition.Manifest.Axes.Count = 1, "axes must be parsed, got " & definition.Manifest.Axes.Count)
            Check(definition.Manifest.OnDemand.Count >= 1, "the on_demand table must be parsed")

            Dim axis = definition.Manifest.Axes(0)
            Check(axis.Values.Count = 2, "the axis values must be parsed, got " & axis.Values.Count)
            Check(axis.DefaultValue = "manuscript", "the axis default must be parsed, got " & axis.DefaultValue)

            Check(fragment.Contains("## Skill Manifest: Optional Axes"), "the manifest summary must be injected")
            Check(fragment.Contains("references/spec.md"), "the on-demand table must list its files")

            ' ---- layer 2: preload budget --------------------------------------
            Check(definition.PreloadedFiles.Any(Function(f) f.RelativePath = "../nature-shared/core/reader-workflow.md"),
                  "a sibling shared package file must be preloaded")
            Check(definition.PreloadedFiles.Any(Function(f) f.RelativePath = "SKILL.md"), "the skill body must be preloaded")
            Check(definition.DeferredResources.Contains("static/core/huge.md"),
                  "a file over the preload budget must be deferred, got " & String.Join(", ", definition.DeferredResources))
            Check(fragment.Contains("## Skill files not preloaded"), "the deferred files must be listed in the prompt")
            Check(fragment.Contains("## Preloaded skill file: SKILL.md"), "the preloaded content must be injected")

            ' ---- layer 3: file reading ----------------------------------------
            Dim reference As String = manager.ReadSkillFile("demo-skill", "references/spec.md")
            Check(reference.Contains("specification marker"), "a regular skill file must be readable: " & reference)

            Dim sharedFile As String = manager.ReadSkillFile("demo-skill", "../nature-shared/core/reader-workflow.md")
            Check(sharedFile.Contains("shared workflow marker"), "a sibling shared package file must be readable: " & sharedFile)

            Dim escaped As String = manager.ReadSkillFile("demo-skill", "../../outside.md")
            Check(escaped.StartsWith("[error]"), "a path escaping the skills root must be rejected: " & escaped)
            Check(escaped.Contains("escapes the skills root"), "the rejection must explain itself: " & escaped)

            Dim missing As String = manager.ReadSkillFile("demo-skill", "references/nope.md")
            Check(missing.Contains("not found"), "a missing file must report not found: " & missing)

            ' ---- layer 3: resource enumeration --------------------------------
            Dim resources As List(Of SkillResource) = manager.EnumerateResources("demo-skill")
            Dim paths As List(Of String) = resources.Select(Function(r) r.RelativePath).ToList()

            Check(paths.Contains("scripts/echo_args.bat"), "scripts must be enumerated")
            Check(paths.Contains("references/spec.md"), "references must be enumerated")
            Check(paths.Contains("notes/extra.md"), "a non standard folder must be enumerated too: " & String.Join(", ", paths))
            Check(paths.All(Function(p) Not p.StartsWith("tests/")), "the excluded folders must be skipped")

            ' ---- layer 3: script execution ------------------------------------
            Dim echoed As ScriptExecutionResult = manager.ExecuteScript("demo-skill", "scripts/echo_args.bat", "--input data.csv --limit 10")
            Check(echoed.Success, "the script must run: " & echoed.Stderr)
            Check(echoed.Stdout.Contains("arg1=--input"), "the first argument must arrive alone: " & echoed.Stdout)
            Check(echoed.Stdout.Contains("arg2=data.csv"), "the second argument must arrive alone: " & echoed.Stdout)
            Check(echoed.Stdout.Contains("arg4=10"), "the fourth argument must arrive alone: " & echoed.Stdout)

            Dim quoted As String = """" & "hello world" & """" & " tail"
            Dim quotedResult As ScriptExecutionResult = manager.ExecuteScript("demo-skill", "scripts/echo_args.bat", quoted)
            Check(quotedResult.Stdout.Contains("arg1=hello world"), "a quoted argument must keep its spaces: " & quotedResult.Stdout)
            Check(quotedResult.Stdout.Contains("arg2=tail"), "the argument after the quote must be separate: " & quotedResult.Stdout)

            Dim crossPackage As ScriptExecutionResult =
                manager.ExecuteScript("demo-skill", "../nature-shared/scripts/echo_args.bat", "shared ok")
            Check(crossPackage.Success, "a script of a sibling shared package must be runnable: " & crossPackage.Stderr)

            Dim evilScript As ScriptExecutionResult = manager.ExecuteScript("demo-skill", "../../evil.bat", "")
            Check(Not evilScript.Success, "a script outside the skills root must be rejected")

            ' a timeout must kill the tree and report it
            manager.Executor.TimeoutMs = 800
            Dim watch As Diagnostics.Stopwatch = Diagnostics.Stopwatch.StartNew()
            Dim slow As ScriptExecutionResult = manager.ExecuteScript("demo-skill", "scripts/slow.bat", "")
            watch.Stop()
            manager.Executor.TimeoutMs = 60000

            Check(Not slow.Success, "a script over the timeout must fail")
            Check(slow.Stderr.Contains("timed out"), "the timeout must be reported: " & slow.Stderr)
            Check(watch.ElapsedMilliseconds < 8000, $"the timeout must be enforced promptly, took {watch.ElapsedMilliseconds}ms")

            ' ---- manifest parsing robustness ----------------------------------
            Dim empty As SkillManifest = SkillManifest.Load(Path.Combine(skills, "no-manifest-skill"))
            Check(Not empty.IsLoaded, "a skill without manifest must yield an unloaded manifest")

            Dim parsed As SkillManifest = SkillManifest.Parse("name: x" & vbLf & "always_load:" & vbLf & "  - a.md" & vbLf)
            Check(parsed.IsLoaded AndAlso parsed.AlwaysLoad.Count = 1, "a minimal manifest must parse")

            Dim emptyDocument As SkillManifest = SkillManifest.Parse("")
            Check(Not emptyDocument.IsLoaded, "an empty manifest must not be reported as loaded")

            Dim threw As Boolean = False

            Try
                Dim malformed As SkillManifest = SkillManifest.Parse("{ name: [unclosed, : -")
                Check(malformed IsNot Nothing, "a malformed manifest must still return a usable model")
            Catch ex As Exception
                threw = True
            End Try

            Check(Not threw, "a malformed manifest must never throw")
        End Sub

        ''' <summary>
        ''' verify a real nature-skills checkout, read only, when one is present.
        ''' </summary>
        Private Sub RunNatureSkillsTests()
            Dim skillsRoot As String = NatureSkillsCandidates.FirstOrDefault(Function(c) Directory.Exists(c))

            If skillsRoot Is Nothing Then
                Console.WriteLine("== nature-skills: no checkout found, skipped")
                Return
            End If

            Console.WriteLine("== nature-skills: " & skillsRoot)

            Dim manager As New SkillManager(skillsRoot)
            Dim scanned As Integer = manager.ScanSkills()

            Check(scanned >= 10, "the nature skills must be scanned, got " & scanned)

            Dim catalog As IReadOnlyList(Of SkillMetadata) = manager.GetRoutableMetadata()
            Check(catalog.Any(Function(m) m.Name = "nature-writing"),
                  "a nature skill must appear in the router catalog, got " & String.Join(", ", catalog.Select(Function(m) m.Name)))

            Dim sharedPackages As List(Of SkillMetadata) =
                manager.GetAllMetadata().Where(Function(m) m.IsSharedPackage).ToList()

            Check(sharedPackages.Count >= 1, "the shared package must be recognised")

            ' the folder name is not always the skill name (nature-proposal-writer declares
            ' "name: researchwrite"), so the deep assertions run on a real catalog entry
            Dim target As SkillMetadata = catalog.FirstOrDefault()
            If target Is Nothing Then
                Console.WriteLine("   (no routable nature skill, skipping the deep assertions)")
                Return
            End If

            Console.WriteLine("   deep assertions on: " & target.Name)

            Check(Not target.Description.Contains(vbLf), "a block scalar description must be folded: " & target.Description)

            Dim definition As SkillDefinition = manager.LoadFullSkill(target.Name)
            Check(definition IsNot Nothing, target.Name & " must load")

            Dim fragment As String = definition.BuildPromptFragment()
            Check(fragment.Length > 500, "its prompt fragment must not be empty, got " & fragment.Length & " characters")
            Check(definition.Manifest IsNot Nothing AndAlso definition.Manifest.IsLoaded, "its manifest must be loaded")
            Check(definition.Manifest.AlwaysLoad.Count >= 1, "its always_load table must be read")

            Dim referenced As String = definition.Manifest.OnDemand.Select(Function(r) r.Path).FirstOrDefault()
            If referenced IsNot Nothing Then
                Dim content As String = manager.ReadSkillFile(target.Name, referenced)
                Check(Not content.StartsWith("[error]"), $"an on-demand manifest file must be readable ({referenced}): {content}")
            End If

            Dim escaped As String = manager.ReadSkillFile(target.Name, "../../../windows/system.ini")
            Check(escaped.StartsWith("[error]"), "a path escaping the skills root must be rejected: " & escaped)
        End Sub

        ' ====================================================================
        ' fixtures
        ' ====================================================================

        Private Sub BuildFixtures()
            Dim skills As String = Path.Combine(_root, "skills")

            ' ---- the shared support package ------------------------------------
            Dim sharedDir As String = Path.Combine(skills, "nature-shared")

            WriteLines(Path.Combine(sharedDir, "SKILL.md"), {
                "---",
                "name: nature-shared",
                "description: >",
                "  Internal shared-reference support package. Do not invoke it as a",
                "  standalone workflow.",
                "---",
                "",
                "# Shared",
                "",
                "See core/reader-workflow.md."
            })

            WriteLines(Path.Combine(sharedDir, "core", "reader-workflow.md"), {
                "# Reader workflow",
                "",
                "shared workflow marker"
            })

            WriteLines(Path.Combine(sharedDir, "scripts", "echo_args.bat"), EchoArgsBatch())

            ' ---- a skill with a manifest, a shared reference and a huge file ----
            Dim demoDir As String = Path.Combine(skills, "demo-skill")

            WriteLines(Path.Combine(demoDir, "SKILL.md"), {
                "---",
                "name: demo-skill",
                "version: 2.0.0",
                "description: >",
                "  A demo skill for planning the release, with a description that",
                "  spans several lines and must be folded.",
                "metadata:",
                "  hermes:",
                "    tags: [demo, release]",
                "---",
                "",
                "# Demo skill",
                "",
                "This skill has no well known section headings at all, so its body",
                "has to be injected. It refers to `references/spec.md` and to",
                "`../nature-shared/core/reader-workflow.md`.",
                "",
                "## How it works",
                "",
                "The body marker BODY-MARKER must be visible in the prompt."
            })

            WriteLines(Path.Combine(demoDir, "manifest.yaml"), {
                "name: demo-skill",
                "version: 2.0.0",
                "description: >",
                "  A demo skill.",
                "always_load:",
                "  - SKILL.md",
                "  - ../nature-shared/core/reader-workflow.md",
                "  - static/core/huge.md",
                "axes:",
                "  task:",
                "    detect: >",
                "      Use manuscript mode when the user already has a draft.",
                "    values:",
                "      manuscript: static/fragments/manuscript.md",
                "      outline: static/fragments/outline.md",
                "    default: manuscript",
                "    multi: false",
                "references:",
                "  on_demand:",
                "    - condition: >",
                "        Read this when the user asks about the specification.",
                "      path: references/spec.md",
                "templates:",
                "  on_demand:",
                "    - condition: Always read before composing.",
                "      path: templates/outline-template.md"
            })

            WriteLines(Path.Combine(demoDir, "references", "spec.md"), {
                "# Specification",
                "",
                "specification marker"
            })

            WriteLines(Path.Combine(demoDir, "templates", "outline-template.md"), {"# Outline template"})

            WriteLines(Path.Combine(demoDir, "static", "core", "small.md"), {"# Small file"})
            WriteLines(Path.Combine(demoDir, "static", "fragments", "manuscript.md"), {"# Manuscript"})

            ' a file well over the single file preload limit
            File.WriteAllText(Path.Combine(demoDir, "static", "core", "huge.md"), New String("x"c, 40000), Encoding.UTF8)

            ' a non standard folder plus a folder that must be skipped
            WriteLines(Path.Combine(demoDir, "notes", "extra.md"), {"# Notes"})
            WriteLines(Path.Combine(demoDir, "tests", "ignored.md"), {"# Ignored"})

            WriteLines(Path.Combine(demoDir, "scripts", "echo_args.bat"), EchoArgsBatch())

            Dim slow As New List(Of String)()
            slow.Add("@echo off")
            slow.Add("ping -n 15 127.0.0.1 >nul")
            slow.Add("echo done")
            WriteLines(Path.Combine(demoDir, "scripts", "slow.bat"), slow)

            ' ---- a skill whose front matter is far longer than 4KB -------------
            Dim longDir As String = Path.Combine(skills, "long-meta-skill")
            Dim longDescription As String = "This description is intentionally long. " & New String("y"c, 6000) & " END-OF-DESCRIPTION"

            WriteLines(Path.Combine(longDir, "SKILL.md"), {
                "---",
                "name: long-meta-skill",
                "description: >",
                "  " & longDescription,
                "---",
                "",
                "# Long metadata"
            })

            ' ---- a skill without manifest (manifest load must not fail) --------
            WriteLines(Path.Combine(skills, "no-manifest-skill", "SKILL.md"), {
                "---",
                "name: no-manifest-skill",
                "description: a plain skill",
                "---",
                "",
                "# Plain"
            })
        End Sub

        ''' <summary>a batch script that prints every received argument on its own line.</summary>
        Private Function EchoArgsBatch() As List(Of String)
            Return New List(Of String) From {
                "@echo off",
                "setlocal enabledelayedexpansion",
                "set i=0",
                ":loop",
                "if ""%~1""=="""" goto :end",
                "set /a i+=1",
                "echo arg!i!=%~1",
                "shift",
                "goto :loop",
                ":end"
            }
        End Function

        Private Sub WriteLines(filePath As String, lines As IEnumerable(Of String))
            Directory.CreateDirectory(Path.GetDirectoryName(filePath))
            File.WriteAllText(filePath, String.Join(ControlChars.CrLf, lines) & ControlChars.CrLf, New UTF8Encoding(False))
        End Sub

        ' ====================================================================
        ' assertions
        ' ====================================================================

        Private Sub Check(condition As Boolean, message As String)
            If condition Then
                _passed += 1
            Else
                Fail(message)
            End If
        End Sub

        Private Sub Fail(message As String)
            _failed += 1
            _failures.Add(message)
            Console.WriteLine("   FAIL: " & message)
        End Sub

    End Module

End Namespace
