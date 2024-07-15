using Godot;
using System;
using System.ComponentModel;
using System.Linq;

public partial class Main : Node2D
{
    public Control ConnectionPanel;
    public Label MessageLabel;
    
    public const int SERVER_PORT = 3456;

    public override void _Ready ()
    {
        ConnectionPanel = GetNode<Control>("CanvasLayer/ConnectionPanel");
        MessageLabel = GetNode<Label>("CanvasLayer/MessageLabel");
        
        Multiplayer.Connect("peer_connected", new Callable(this, nameof(OnNetworkPeerConnected)));
        Multiplayer.Connect("peer_disconnected", new Callable(this, nameof(OnNetworkPeerDisconnected)));
        Multiplayer.Connect("server_disconnected", new Callable(this, nameof(OnServerDisconnected)));
        SyncManager.singleton.Connect(nameof(SyncManager.SyncStarted), new Callable(this, nameof(OnSyncManagerSyncStarted)));
        SyncManager.singleton.Connect(nameof(SyncManager.SyncLost), new Callable(this, nameof(OnSyncManagerSyncLost)));
        SyncManager.singleton.Connect(nameof(SyncManager.SyncRegained), new Callable(this, nameof(OnSyncManagerSyncRegained)));
        SyncManager.singleton.Connect(nameof(SyncManager.SyncError), new Callable(this, nameof(OnSyncManagerSyncError)));

        var cmdlineArgs = OS.GetCmdlineArgs();
        if(cmdlineArgs.Contains("server"))
            _on_ServerButton_pressed();
        else if (cmdlineArgs.Contains("client"))
            _on_ClientButton1_pressed();

    }

    public void _on_ServerButton_pressed ()
    {
        var peer = new ENetMultiplayerPeer();
        peer.CreateServer(SERVER_PORT, 2);
        
        Upnp upnp = new Upnp();
        upnp.Discover();
        upnp.AddPortMapping(SERVER_PORT);
        
        Multiplayer.MultiplayerPeer = peer;
        ConnectionPanel.Visible = false;
    }

    public void _on_ClientButton1_pressed ()
    {
        var peer = new ENetMultiplayerPeer();
        peer.CreateClient(GetNode<LineEdit>("CanvasLayer/ConnectionPanel/Address").Text, SERVER_PORT);
        Multiplayer.MultiplayerPeer = peer;
        ConnectionPanel.Visible = false;
        MessageLabel.Text = "Connecting...";
    }

    public async void OnNetworkPeerConnected (int peerID)
    {
        GD.Print("peer " + peerID + " connected");
        
        GetNode("ServerPlayer").SetMultiplayerAuthority(1);
        if (Multiplayer.IsServer())
        {
            GetNode("ClientPlayer").SetMultiplayerAuthority(peerID);
        }
        else
        {
            GetNode("ClientPlayer").SetMultiplayerAuthority(Multiplayer.GetUniqueId());
        }
        
        SyncManager.singleton.AddPeer(peerID);

        if (Multiplayer.IsServer())
        {
            MessageLabel.Text = "Starting...";
            await ToSignal(GetTree().CreateTimer(2.0f), "timeout");
            SyncManager.singleton.Start();
        }
    }
    
    public void OnNetworkPeerDisconnected (int peerID)
    {
        
        MessageLabel.Text = "Disconnected";
        SyncManager.singleton.RemovePeer(peerID);
    }
    
    public void OnServerDisconnected ()
    {
        OnNetworkPeerDisconnected(1);
    }
    
    public void OnSyncManagerSyncStarted ()
    {
        MessageLabel.Text = "Started !";
    }
    
    public void OnSyncManagerSyncLost ()
    {
        MessageLabel.Text = "Re-syncing...";
    }
    
    public void OnSyncManagerSyncRegained ()
    {
        MessageLabel.Text = "";
    }
    
    public void OnSyncManagerSyncError (string msg)
    {
        MessageLabel.Text = "Fatal sync error: " + msg;
        var peer = Multiplayer.MultiplayerPeer;
        if (peer != null)
        {
            
        }
        SyncManager.singleton.ClearPeers();
    }
}
