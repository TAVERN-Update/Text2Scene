using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public class SceneObject
{
    public string name;
    public string description;
    public string visual_description;
    public int quantity = 1;
    public Position position;
}

[Serializable]
public class Position
{
    public float x, y, z;
}

[Serializable]
public class SceneData
{
    public List<SceneObject> objects;
    public string scene_name;
    public string scene_description;
    public string visual_style;
}
