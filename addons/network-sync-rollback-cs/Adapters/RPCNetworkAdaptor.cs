using Godot;

/// <summary>
/// Example Network Adapter
/// Use the built-in multiplayer HLAPI & RPCs
/// </summary>
public partial class RPCNetworkAdaptor : NetworkAdaptor
{
    public override void SendInputTick (int peerID, byte[] msg)
    {
        RpcId(peerID, nameof(RIT), msg);
    }

    public override void PingPeer (int peerID, byte[] pingInformations)
    {
        RpcId(peerID, nameof(Ping), pingInformations);
    }

    public override void PingBackPeer (int peerID, byte[] pingInformations)
    {
        RpcId(peerID, nameof(PingBack), pingInformations);
    }
    
    [Rpc(MultiplayerApi.RpcMode.AnyPeer)]
    public void RIT (byte[] msg)
    {
        EmitSignal(nameof(ReceivedInputTick), Multiplayer.GetRemoteSenderId(), msg);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer)]
    public void Ping (byte[] msg)
    {
        EmitSignal(nameof(Pinged), Multiplayer.GetRemoteSenderId(), msg);
    }
    
    [Rpc(MultiplayerApi.RpcMode.AnyPeer)]
    public void PingBack (byte[] msg)
    {
        EmitSignal(nameof(PingedBack), Multiplayer.GetRemoteSenderId(), msg);
    }
}
