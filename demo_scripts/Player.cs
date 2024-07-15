using Godot;
using System;
using System.Linq;
using Godot.Collections;

public partial class Player : Sprite2D, INetworkable, INetworkedInputs
{
    float Lerp(float firstFloat, float secondFloat, float by)
    {
        return firstFloat * (1 - by) + secondFloat * by;
    }
    
    Vector2 Lerp(Vector2 firstVector, Vector2 secondVector, float by)
    {
        float retX = Lerp(firstVector.X, secondVector.X, by);
        float retY = Lerp(firstVector.Y, secondVector.Y, by);
        return new Vector2(retX, retY);
    }
    
    public Vector2 ReadString (string str)
    {
        return new Vector2
        (
            float.Parse(str.Split('|')[0]),
            float.Parse(str.Split('|')[1])
    );
}
    public Dictionary<string, string> SaveState ()
    {
        return new Dictionary<string, string>
        {
            {"position", Position.X + "|" + Position.Y}
        };
    }

    public void LoadState (Dictionary<string, string> state)
    {
        Position = ReadString(state["position"]);
    }

    public void InterpolateState (Dictionary<string, string> oldState, Dictionary<string, string> newState, float weight)
    {
        Position = Lerp(ReadString(oldState["position"]), ReadString(newState["position"]), weight);
    }

    public NodeInputs GetLocalInput ()
    {
        Vector2 axis = new Vector2(
            Input.GetActionStrength("player_right") - Input.GetActionStrength("player_left"),
            Input.GetActionStrength("player_down") - Input.GetActionStrength("player_up"));

        NodeInputs input = new NodeInputs();

        if (axis != Vector2.Zero)
            input[0] = axis.X + "|" + axis.Y;

        return input;
    }

    public NodeInputs PredictRemoteInput (NodeInputs previousInput, int ticksSinceRealInput)
    {
        NodeInputs inputs = previousInput.Duplicate();

        if (ticksSinceRealInput > 5)
            inputs.inputs.Remove(0);

        return inputs;
    }

    public void NetworkTick (float delta, NodeInputs input)
    {
        Vector2 axis = new Vector2();
        if(input.inputs.Count > 0)
            axis = ReadString(input[0]);
        
        Position += axis * 16;
    }
}
