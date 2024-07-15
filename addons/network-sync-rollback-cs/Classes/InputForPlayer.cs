using Godot;
using Godot.Collections;

public partial class InputForPlayer : RefCounted
{
    public LocalPeerInputs Input;
    public bool Predicted;

    public InputForPlayer (LocalPeerInputs _input, bool _predicted)
    {
        Input = _input;
        Predicted = _predicted;
    }
}