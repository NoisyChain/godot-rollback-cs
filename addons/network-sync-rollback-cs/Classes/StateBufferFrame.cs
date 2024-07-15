using Godot;
using Godot.Collections;

public partial class StateBufferFrame : RefCounted
{
    public int Tick;
    public Dictionary<string, Dictionary<string, string>> Data;

    public StateBufferFrame (int _tick, Dictionary<string, Dictionary<string, string>> _data)
    {
        Tick = _tick;
        Data = _data;
    }
}