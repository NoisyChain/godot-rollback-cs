using Godot;
using System;

/// <summary>
/// Abstract class to make your own network adaptor
/// You'll need to provide way to send crucial data for the SyncManager
/// </summary>
public abstract partial class NetworkAdaptor : Node
{
    [Signal] public delegate void ReceivedInputTickEventHandler (int peerID, byte[] msg);
    [Signal] public delegate void PingedEventHandler (int peerID, byte[] msg);
    [Signal] public delegate void PingedBackEventHandler (int peerID, byte[] msg);
    
    public virtual void Attach (SyncManager manager) {}
    public virtual void Detach (SyncManager manager) {}
    public virtual void Start (SyncManager manager) {}
    public virtual void Stop (SyncManager manager) {}
    public virtual void Poll () {}
    
    public abstract void SendInputTick (int peerID, byte[] msg);
    public abstract void PingPeer (int peerID, byte[] pingInformations);
    public abstract void PingBackPeer (int peerID, byte[] pingInformations);
    
}
