/* ==========================================================================
   jquery.jsonview 的最小 jQuery 兼容层
   --------------------------------------------------------------------------
   jquery.jsonview.min.js 是一个 jQuery 插件，而日志查看器不引入整个
   jQuery。这个文件提供它真正用到的那一小块表面：

     jQuery(element | '#id' | '.class')   ->  Mini collection
         .each(fn)        fn.call(element, index, element)
         .find(selector)  后代搜索（支持 "a b" 后代 与 "a > b" 父子）
         .siblings(sel)   兄弟元素（按 class 过滤，不含自身）
         .html(value)     设置 innerHTML
         [0] / .length    原生元素与长度
     jQuery.fn            Mini 的原型，插件往上面挂 JSONView
     jQuery.extend(t, s)  浅合并
     jQuery.inArray(v, a) 下标（插件仅在 strict 模式下用到）

   选择器只实现插件实际出现的形态：".a"、".a.b"、"tag"、".jsonview .collapsible.level3"
   与 ".jsonview > ul > li .collapsible"，够用且不需要一个完整的选择器引擎。
   ========================================================================== */
(function (window) {
    'use strict';

    function toElements(arg) {
        if (!arg) return [];
        if (arg.nodeType) return [arg];                       // 原生元素
        if (arg instanceof Mini) return arg.elements.slice(); // 已是集合
        if (typeof arg !== 'string') return [];

        var text = arg.trim();

        if (text.charAt(0) === '<') {
            // 片段：'<div>x</div>'
            var holder = document.createElement('div');
            holder.innerHTML = text;
            return Array.prototype.slice.call(holder.children);
        }

        // 入口选择器交给原生引擎即可（这里只会是 #id 这类简单选择器）
        if (isSimple(text)) {
            return Array.prototype.slice.call(document.querySelectorAll(text));
        }

        return search(document, text);
    }

    function isSimple(selector) {
        return !/[ >]/.test(selector);
    }

    function matchesSimple(element, selector) {
        if (!selector || selector === '*') return true;

        var parts = selector.split('.');
        var tag = parts[0].toUpperCase();

        if (tag && tag !== '*' && element.nodeName !== tag) return false;

        for (var i = 1; i < parts.length; i++) {
            if (!parts[i]) continue;
            if (!element.classList || !element.classList.contains(parts[i])) return false;
        }

        return true;
    }

    function parseSelector(selector) {
        var tokens = selector.trim().split(/\s+/);
        var steps = [];
        var combinator = null;

        for (var i = 0; i < tokens.length; i++) {
            if (tokens[i] === '>') {
                combinator = '>';
                continue;
            }
            steps.push({ combinator: combinator, simple: tokens[i] });
            combinator = ' ';
        }

        return steps;
    }

    function children(element) {
        return Array.prototype.slice.call(element.children);
    }

    function descendants(element) {
        var out = [];
        var stack = children(element);

        while (stack.length) {
            var node = stack.shift();
            out.push(node);
            stack = children(node).concat(stack);
        }

        return out;
    }

    function search(root, selector) {
        var steps = parseSelector(selector);
        var current = [root];
        var first = true;

        for (var i = 0; i < steps.length; i++) {
            var step = steps[i];
            var pool = [];

            for (var j = 0; j < current.length; j++) {
                var candidates = first ? descendants(current[j])
                    : (step.combinator === '>' ? children(current[j]) : descendants(current[j]));

                for (var k = 0; k < candidates.length; k++) {
                    pool.push(candidates[k]);
                }
            }

            var matched = [];

            for (var m = 0; m < pool.length; m++) {
                if (matchesSimple(pool[m], step.simple)) matched.push(pool[m]);
            }

            current = matched;
            first = false;

            if (!current.length) break;
        }

        return current;
    }

    function Mini(elements) {
        this.elements = elements;
        this.length = elements.length;

        for (var i = 0; i < elements.length; i++) {
            this[i] = elements[i];
        }
    }

    Mini.prototype.each = function (fn) {
        for (var i = 0; i < this.elements.length; i++) {
            fn.call(this.elements[i], i, this.elements[i]);
        }
        return this;
    };

    Mini.prototype.find = function (selector) {
        var out = [];

        for (var i = 0; i < this.elements.length; i++) {
            out = out.concat(search(this.elements[i], selector));
        }

        return new Mini(out);
    };

    Mini.prototype.siblings = function (selector) {
        var out = [];

        for (var i = 0; i < this.elements.length; i++) {
            var el = this.elements[i];
            var parent = el.parentNode;

            if (!parent) continue;

            var brothers = children(parent);

            for (var j = 0; j < brothers.length; j++) {
                if (brothers[j] === el) continue;
                if (selector && !matchesSimple(brothers[j], selector)) continue;
                out.push(brothers[j]);
            }
        }

        return new Mini(out);
    };

    Mini.prototype.html = function (value) {
        if (value === undefined) {
            return this.elements.length ? this.elements[0].innerHTML : null;
        }

        for (var i = 0; i < this.elements.length; i++) {
            this.elements[i].innerHTML = value;
        }

        return this;
    };

    var jQuery = function (arg) {
        return new Mini(toElements(arg));
    };

    // 插件往 jQuery.fn 上挂方法，因此 fn 必须是集合对象的原型
    jQuery.fn = Mini.prototype;

    jQuery.extend = function (target, source) {
        if (!source) return target;

        for (var key in source) {
            if (Object.prototype.hasOwnProperty.call(source, key)) {
                target[key] = source[key];
            }
        }

        return target;
    };

    jQuery.inArray = function (value, array) {
        if (!array) return -1;
        return Array.prototype.indexOf.call(array, value);
    };

    window.jQuery = window.$ = jQuery;
})(window);
