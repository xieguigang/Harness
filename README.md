# A LLMs harness toolkit written in VisualBasic.NET language

###### Hidden-Markov-Model in VisualBasic.

This library was adapted from github repository: [https://github.com/AhmedHani/Hidden-Markov-Model]()

**A VisualBasic implementation of Hidden Markov Model.**

The implementation contains ``Brute Force``, ``Forward-backward``, ``Viterbi`` and ``Baum-Welch`` algorithms.

Hidden Markov Model is a classifier that is used in different way than the other Machine Learning classifiers. HMM depends on sequences that are shown during sequential time instants. It has many applications such as weather predictions and shines in Speech recognition applications.

## Implemented (continuously updating)
* Json reader
* Data Validation for an HMM
* Forward-Backward Algorithm
* Viterbi Algorithm
* Baum-Welch Algorithm

## Json Reader
You can create your model using Json files. In the repository, you will see an example of a model written in a specific expression.
The model parser can be found at namespace:

**Microsoft.VisualBasic.DataMining.HMM.Model.JsonHMM**

I added this feature to help the user to avoid the hard-coding part when entering the model data such as transition and emission matrices. The Json file is divided to 2 parts, the model info and model data. In model data, you put some information about the model, this enables you when you deal with large amount of models in your projects.

```json
  "modelInfo": {
    "date": "4\/15\/2016 9:20:51 AM",
    "name": "TestData",
    "title": "Test.Main::TestData"
  }
```

You can manually change these data as you wish.

The second part is the model data which is the core of the HMM

```json
  "modelData": {
    "emissions": [ "R->F->0.4", "R->U->0.5", "R->D->0.1", "S->F->0.4", "S->U->0.0", "S->D->0.6", "C->F->0.4", "C->U->0.2", "C->D->0.4" ],
    "init_props": [
      {
        "Key": "R",
        "Value": 0.3
      },
      {
        "Key": "S",
        "Value": 0.4
      },
      {
        "Key": "C",
        "Value": 0.3
      }
    ],
    "observations": [ "F", "U", "D" ],
    "states": [ "R", "S", "C" ],
    "transitions": [ "R->R->0.2", "R->S->0.1", "R->C->0.7", "S->R->0.3", "S->S->0.4", "S->C->0.3", "C->R->0.1", "C->S->0.4", "C->C->0.5" ]
  }
```

![alt tag](https://ahmedhanibrahim.files.wordpress.com/2015/08/hmm1.png)

## How to use

First of all, you should make an instance of the HMM class

```
Dim hmm As New HiddenMarkovModel(name, states, observations, initialProbabilities, transitionMatrix, emissionMatrix)
```
You can create the HMM constructor parameters using 2 ways
+ Put your model data in a json file, then read it like that
```vbnet
Dim jp As JsonHMM = "F:\HMM\Resources\test_HMM.json".ReadAllText.LoadObject(Of JsonHMM)
Dim name As String = jp.modelInfo.name
Dim states As List(Of String) = New List(Of String)(jp.modelData.states)
Dim observations As List(Of String) = New List(Of String)(jp.modelData.observations)
Dim initialProbabilities As Dictionary(Of String, Double) = jp.modelData.init_props
Dim transitionMatrix As Dictionary(Of String, Double) = DataDecoding.GetMatrix(jp.modelData.transitions)
Dim emissionMatrix As Dictionary(Of String, Double) = DataDecoding.GetMatrix(jp.modelData.emissions)
```
+ Hard-code your parameters by setting the elements one by one for each parameter

## Tutorials
[https://ahmedhanibrahim.wordpress.com/2015/10/25/hidden-markov-models-hmms-part-i/](https://ahmedhanibrahim.wordpress.com/2015/10/25/hidden-markov-models-hmms-part-i/)
