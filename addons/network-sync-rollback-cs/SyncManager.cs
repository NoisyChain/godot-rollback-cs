using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Dictionary = Godot.Collections.Dictionary;
using Array = Godot.Collections.Array;
using G = Godot.Collections;

enum InputMessageKey 
{
    TICK,
    NEXT_TICK_REQUESTED,
    INPUT,
}

/// <summary>
/// This beast handle all the sync & rollback logic
/// Singleton pattern
/// </summary>
public partial class SyncManager : Node
{
    public static SyncManager singleton;

    private NetworkAdaptor networkAdaptor;
    
    private G.Dictionary<int, Peer> _peers = new G.Dictionary<int, Peer>();
    private List<InputBufferFrame> _inputBuffer = new List<InputBufferFrame>();
    private List<StateBufferFrame> _stateBuffer = new List<StateBufferFrame>();

    public const int MAX_BUFFER_SIZE = 30;
    public const int TICKS_TO_RECALCULATE_ADVANTAGE = 60;
    public const int INPUT_DELAY = 2;
    public const int MAX_INPUT_FRAMES_PER_MESSAGE = 5;
    public const int MAX_MESSAGE_AT_ONCE = 2;
    public const int MAX_INPUT_BUFFER_UNDERRUNS = 300;
    public const int SKIP_TICKS_AFTER_SYNC_REGAINED = 2;
    public const bool INTERPOLATION = true;
    public const int ROLLBACK_DEBUG_TICKS = 5;
    public const int DEBUG_MESSAGE_BYTE = 700;
    public const bool LOG_STATE = true;

    private float PING_FREQUENCY = 1.0f;

    public int inputTick { get; private set; }
    public int currentTick { get; private set; }
    public int skipTicks { get; private set; }
    public int rollbackTicks { get; private set; }
    public int inputBufferUnderruns { get; private set; }
    public bool started { get; private set; }

    private Timer _pingTimer;
    private SpawnManager _spawnManager;
    private float _tickTime;
    private int _inputBufferStartTick;
    private int _stateBufferStartTick;
    private int _inputSendQueueStartTick = 1;
    private List<byte[]> _inputSendQueue = new List<byte[]>();
    private float timeSinceLastTick;
    private G.Dictionary<int, G.Array<StateBufferFrame>> _loggedRemoteState = new G.Dictionary<int, G.Array<StateBufferFrame>>();
    private G.Dictionary<string, G.Array<G.Dictionary<string, string>>> interpolationState = new G.Dictionary<string, G.Array<G.Dictionary<string, string>>>();

    [Signal] public delegate void SyncStartedEventHandler ();
    [Signal] public delegate void SyncStoppedEventHandler ();
    [Signal] public delegate void SyncLostEventHandler ();
    [Signal] public delegate void SyncRegainedEventHandler ();
    [Signal] public delegate void SyncErrorEventHandler (string msg);
    
    [Signal] public delegate void SkipTickFlaggedEventHandler (int tick);
    [Signal] public delegate void RollbackFlaggedEventHandler (int tick, int peerId, LocalPeerInputs localInput, LocalPeerInputs remoteInput);
    [Signal] public delegate void RemoteStateMismatchEventHandler (int tickn, int peerId, StateBufferFrame localState, StateBufferFrame remoteState);
    
    [Signal] public delegate void PeerAddedEventHandler (int id);
    [Signal] public delegate void PeerRemovedEventHandler (int id);
    [Signal] public delegate void PeerPingedBackEventHandler (Peer peer);

    [Signal] public delegate void StateLoadedEventHandler (int rollbackTicks);
    [Signal] public delegate void TickFinishedEventHandler (bool isRollback);
    [Signal] public delegate void SceneSpawnedEventHandler (string name, Node spawnedNode, PackedScene scene, Dictionary data);

    private void _Debug (int tick, int peerID, LocalPeerInputs localInput, LocalPeerInputs remoteInput)
    {
        GD.Print("-----");
        GD.Print("Correcting prediction on tick " + tick + " for peer " + peerID + " (rollback " +
                 SyncManager.singleton.rollbackTicks + " tick(s))");
        GD.Print("Received input: " + remoteInput);
        GD.Print("Predicted input: " + localInput);
    }

    public override void _Ready ()
    {
        singleton = this;
        Connect(nameof(RollbackFlagged), new Callable(this, nameof(_Debug)));
        
        // TODO move to NetworkAdaptor
        Multiplayer.Connect("peer_disconnected", new Callable(this, nameof(RemovePeer)));
        Multiplayer.Connect("server_disconnected", new Callable(this, nameof(Stop)));
        
        _pingTimer = new Timer();
        _pingTimer.WaitTime = PING_FREQUENCY;
        _pingTimer.Autostart = true;
        _pingTimer.OneShot = false;
        _pingTimer.ProcessMode = ProcessModeEnum.Always;//PauseModeEnum.Process;
        _pingTimer.Connect("timeout", new Callable(this, nameof(OnPingTimerTimeout)));
        AddChild(_pingTimer);

        _spawnManager = new SpawnManager();
        _spawnManager.Name = "SpawnManager";
        AddChild(_spawnManager);
        _spawnManager.Connect(nameof(SpawnManager.SceneSpawned), new Callable(this, nameof(OnSpawnManagerSceneSpawned)));

        if (networkAdaptor == null)
        {
            SetNetworkAdaptor(new RPCNetworkAdaptor());
        }
    }

    public void SetNetworkAdaptor (NetworkAdaptor adaptor)
    {
        if (networkAdaptor != null)
        {
            networkAdaptor.Detach(this);
            networkAdaptor.Disconnect(nameof(NetworkAdaptor.ReceivedInputTick), new Callable(this, nameof(ReceiveInputTick)));
            RemoveChild(networkAdaptor);
            networkAdaptor.QueueFree();
        }

        networkAdaptor = adaptor;
        networkAdaptor.Name = "Network Adaptor";
        AddChild(networkAdaptor);
        networkAdaptor.Connect(nameof(NetworkAdaptor.ReceivedInputTick), new Callable(this, nameof(ReceiveInputTick)));
        networkAdaptor.Connect(nameof(NetworkAdaptor.Pinged), new Callable(this, nameof(OnPinged)));
        networkAdaptor.Connect(nameof(NetworkAdaptor.PingedBack), new Callable(this, nameof(OnPingedBack)));
        networkAdaptor.Attach(this);
    }

    public void SetPingFrequency (int _pingFrequency)
    {
        PING_FREQUENCY = _pingFrequency;
        if (_pingTimer != null)
        {
            _pingTimer.WaitTime = PING_FREQUENCY;
        }
    }
    
    public void AddPeer (int id)
    {
        Debug.Assert(!_peers.ContainsKey(id), "Peer with ID: " + id + " already exists");
        Debug.Assert(id != Multiplayer.GetUniqueId(), "Cannot add ourselves in the SyncManager !");

        if (_peers.ContainsKey(id))
            return;
        if (id == Multiplayer.GetUniqueId())
            return;
        
        _peers[id] = new Peer(id);
        EmitSignal(nameof(PeerAdded), id);
    }

    public bool HasPeer (int id)
    {
        return _peers.ContainsKey(id);
    }

    public Peer GetPeer (int id)
    {
        return _peers[id];
    }

    public void RemovePeer (int id)
    {
        if (_peers.ContainsKey(id))
        {
            _peers.Remove(id);
            EmitSignal(nameof(PeerRemoved), id);
        }
        if(_peers.Count == 0)
            Stop();
    }

    public void ClearPeers ()
    {
        foreach (int id in _peers.Duplicate().Keys)
            _peers.Remove(id);
    }

    // Ping other players every seconds
    private void OnPingTimerTimeout ()
    {
        // get the current timestamp
        ulong systemTime = Time.GetTicksMsec();
        // loop through every peers
        foreach (int id in _peers.Keys)
        {
            if (id == Multiplayer.GetUniqueId()) continue;
            
            // Create a dictionary with the timestamp
            Dictionary msg = new Dictionary()
            {
                { "local_time", systemTime.ToString() }
            };
            // Send it through the NetAdapter
            networkAdaptor.PingPeer(id, GD.VarToBytes(msg));
        }
    }

    // When someone ping us
    private void OnPinged (int peerID, byte[] message)
    {
        // Deserialize
        Dictionary pingInfo = (Dictionary)GD.BytesToVar(message);
        
        if (peerID == Multiplayer.GetUniqueId()) return;
        
        // Add a new entry with the actual timestamp
        pingInfo["remote_time"] = Time.GetTicksMsec().ToString();
        // Send back a ping request
        networkAdaptor.PingBackPeer(peerID, GD.VarToBytes(pingInfo));
    }

    // When someone pinged us back
    private void OnPingedBack (int peerID, byte[] message)
    {
        Dictionary pingInfo = (Dictionary)GD.BytesToVar(message);
        
        ulong systemTime = Time.GetTicksMsec();
        int peerId = Multiplayer.GetRemoteSenderId();
        Peer peer = _peers[peerId];

        peer.LastPingReceived = systemTime;
        peer.RTT = systemTime - ulong.Parse((string)pingInfo["local_time"]);
        peer.DeltaTime = long.Parse((string)pingInfo["remote_time"]) - long.Parse((string)pingInfo["local_time"]) - (long)(peer.RTT / 2.0);
        
        EmitSignal(nameof(PeerPingedBack), peer);
    }

    public async void Start ()
    {
        Debug.Assert(Multiplayer.IsServer(), "Start() should only be called on the host");
        if (started)
            return;
        
        if (Multiplayer.IsServer())
        {
            ulong highestRtt = 0;
            foreach (Peer peer in _peers.Values)
                highestRtt = Math.Max(highestRtt, peer.RTT);
            
            Rpc(nameof(RemoteStart));
            
            GD.Print("Delaying host start by " + (highestRtt / 2) + " ms");
            await ToSignal(GetTree().CreateTimer((float) (highestRtt / 2000.0)), "timeout");
            RemoteStart();
        }
    }

    public void Reset ()
    {
        inputTick = 0;
        currentTick = inputTick - INPUT_DELAY;
        skipTicks = 0;
        rollbackTicks = 0;
        inputBufferUnderruns = 0;
        _inputBuffer.Clear();
        _stateBuffer.Clear();
        _inputBufferStartTick = 1;
        _stateBufferStartTick = 0;
        _inputSendQueue.Clear();
        _inputSendQueueStartTick = 1;
        interpolationState.Clear();
        timeSinceLastTick = 0.0f;
        _loggedRemoteState.Clear();
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true)]
    private void RemoteStart ()
    {
        Reset();
        _tickTime = (1.0f / Engine.PhysicsTicksPerSecond);
        started = true;
        networkAdaptor.Start(this);
        EmitSignal(nameof(SyncStarted));
    }

    public void Stop ()
    {
        if (Multiplayer.IsServer())
        {
            Rpc(nameof(RemoteStop));
        }
        else
        {
            RemoteStop();
        }
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true)]
    private void RemoteStop ()
    {
        networkAdaptor.Stop(this);
        started = false;
        Reset();
        
        foreach(Peer peer in _peers.Values)
            peer.Clear();
        
        EmitSignal(nameof(SyncStopped));
    }

    private void HandleFatalError (string msg)
    {
        EmitSignal(nameof(SyncError), msg);
        GD.PrintErr("NETWORK SYNC LOST: " + msg);
        Stop();
    }

    private LocalPeerInputs CallGetLocalInput ()
    {
        LocalPeerInputs input = new LocalPeerInputs();
        var nodes = GetTree().GetNodesInGroup("network_sync");
        
        foreach (Node node in nodes)
        {
            if (node.IsMultiplayerAuthority() && node is INetworkedInputs networkedInputs && node.IsInsideTree())
            {
                var nodeInput = networkedInputs.GetLocalInput();
                if (nodeInput.inputs.Count > 0)
                    input[node.GetPath().ToString()] = nodeInput;
            }
        }
        
        return input;
    }

    private LocalPeerInputs CallPredictNetworkInput (LocalPeerInputs previousInput, int ticksSinceRealInput)
    {
        LocalPeerInputs input = new LocalPeerInputs();
        var nodes = GetTree().GetNodesInGroup("network_sync");

        foreach (Node node in nodes)
        {
            if (node.IsMultiplayerAuthority()) continue;

            var nodePathStr = node.GetPath().ToString();
            if (previousInput.NodeInputsMap.ContainsKey(nodePathStr))
            {
                var previousInputForNode = previousInput.NodeInputsMap.ContainsKey(nodePathStr) ? previousInput[nodePathStr] : new NodeInputs();
                var predictedInputForNode = previousInputForNode.Duplicate();
                if (node is INetworkedInputs networkedInputs)
                {
                    predictedInputForNode =
                        networkedInputs.PredictRemoteInput(previousInputForNode, ticksSinceRealInput);
                }
                
                if (predictedInputForNode.inputs.Count > 0)
                    input[nodePathStr] = predictedInputForNode;
            }
            
        }

        return input;
    }
    
    public void CallNetworkProcess (float delta, InputBufferFrame inputFrame)
    {
        var nodes = GetTree().GetNodesInGroup("network_sync");
        var i = nodes.Count;

        while (i > 0)
        {
            i -= 1;
            Node node = nodes[i] as Node;
            if (node is INetworkable p && node.IsInsideTree())
            {
                var playerInput = inputFrame.GetPlayerInput(node.GetMultiplayerAuthority());
                p.NetworkTick(delta, (playerInput.NodeInputsMap.ContainsKey(node.GetPath().ToString()) ? playerInput[node.GetPath().ToString()]: new NodeInputs()));
            }
        }
    }

    public G.Dictionary<string, G.Dictionary<string, string>> CallSaveState ()
    {
        G.Dictionary<string, G.Dictionary<string, string>> state = new G.Dictionary<string, G.Dictionary<string, string>>();
        var nodes = GetTree().GetNodesInGroup("network_sync");

        foreach (Node node in nodes)
        {
            if (node is INetworkable networkable && node.IsInsideTree() && !node.IsQueuedForDeletion())
            {
                if(node.GetPath().ToString() != "")     
                    state[node.GetPath().ToString()] = networkable.SaveState();
            }
        }

        return state;
    }

    public void CallLoadState (G.Dictionary<string, G.Dictionary<string, string>> state)
    {
        foreach (string path in state.Keys)
        {
            Debug.Assert(HasNode(path), "Unable to restore state due to missing node");
            if (HasNode(path))
            {
                var node = GetNode(path);

                if (node is INetworkable networkable)
                {
                    networkable.LoadState(state[path]);
                }
            }
        }
    }

    public void CallInterpolateFrame (float weight)
    {
        foreach(string nodePath in interpolationState.Keys)
        {
            if (HasNode(nodePath))
            {
                var node = GetNode(nodePath);
                if (node is INetworkable networkable)
                {
                    var states = interpolationState[nodePath];
                    networkable.InterpolateState(states[0], states[1], weight);
                }
            }
        }
    }

    public void SaveCurrentState ()
    {
        Debug.Assert(currentTick >= 0, "Attemtping to store state for negative tick");
        if (currentTick < 0) return;

        var stateData = CallSaveState();
        _stateBuffer.Add(new StateBufferFrame(currentTick, stateData));

        if(LOG_STATE && !Multiplayer.IsServer() && IsPlayerInputComplete(currentTick))
        {
            RpcId(1, nameof(LogSavedState), currentTick, stateData);
        }
    }
    
    public void DoTick (float delta, bool isRollback = false)
    {
        InputBufferFrame inputFrame = GetInputFrame(currentTick);
        InputBufferFrame previousFrame = GetInputFrame(currentTick - 1);

        Debug.Assert(inputFrame != null, "Input frame for current_tick is null");
            
        foreach (int peerId in _peers.Keys)
        {
            if (!inputFrame.Players.ContainsKey(peerId) || inputFrame.Players[peerId].Predicted)
            {
                LocalPeerInputs predictedInput = new LocalPeerInputs();
                if (previousFrame != null)
                {
                    Peer peer = _peers[peerId];
                    var ticksSinceRealInput = currentTick - peer.LastRemoteTickReceived;
                    predictedInput = CallPredictNetworkInput(previousFrame.GetPlayerInput(peerId), ticksSinceRealInput);
                }
                CalculateInputHash(predictedInput);
                inputFrame.Players[peerId] = new InputForPlayer(predictedInput, true);
            }
        }
            
        CallNetworkProcess(delta, inputFrame);
        SaveCurrentState();

        EmitSignal(nameof(TickFinished), isRollback);
    }

    public InputBufferFrame GetOrCreateInputFrame (int tick)
    {
        InputBufferFrame inputFrame = null;
        
        if (_inputBuffer.Count == 0)
        {
            inputFrame = new InputBufferFrame(tick);
            _inputBuffer.Add(inputFrame);
        }
        else if (tick > _inputBuffer[_inputBuffer.Count - 1].Tick)
        {
            var highest = _inputBuffer[_inputBuffer.Count - 1].Tick;
            while (highest < tick)
            {
                highest += 1;
                inputFrame = new InputBufferFrame(highest);
                _inputBuffer.Add(inputFrame);
            }
        }
        else
        {
            inputFrame = GetInputFrame(tick);
            if (inputFrame == null)
            {
                HandleFatalError("Requested input frame " + tick + " not found in buffer");
                return null;
            }
        }

        return inputFrame;
    }

    public bool CleanupBuffers ()
    {
        var minNextTickRequested = CalculateMinimumNextTickRequested();
        while (_inputSendQueueStartTick < minNextTickRequested)
        {
            _inputSendQueue.RemoveAt(0);
            _inputSendQueueStartTick += 1;
        }
        
        while (_stateBuffer.Count > MAX_BUFFER_SIZE)
        {
            var stateFrameToRetire = _stateBuffer[0];
            var inputFrame = GetInputFrame(stateFrameToRetire.Tick + 1);

            if (inputFrame == null || !inputFrame.IsComplete(_peers))
            {
                Peer[] missing = inputFrame.GetMissingPeers(_peers);
                GD.PrintErr("Attempting to retire state frame " + stateFrameToRetire.Tick + ", but input frame " + inputFrame.Tick + " is still missing input (missing peer(s): " + missing + ")");
                return false;
            }

            _stateBuffer.RemoveAt(0);
            _stateBufferStartTick += 1;
        }

        while (currentTick - _inputBufferStartTick > MAX_BUFFER_SIZE)
        {
            _inputBufferStartTick += 1;
            _inputBuffer.RemoveAt(0);
        }

        return true;
    }
    
    public InputBufferFrame GetInputFrame (int tick)
    {
        if (tick < _inputBufferStartTick)
        {
            return null;
        }

        int index = tick - _inputBufferStartTick;
        if (index >= _inputBuffer.Count)
            return null;
        
        InputBufferFrame inputFrame = _inputBuffer[index];
        
        Debug.Assert(inputFrame.Tick == tick, "Input frame retreived from input buffer has mismatched tick number");
        return inputFrame;
    }

    public LocalPeerInputs GetLatestInputFromPeer (int peerID)
    {
        if (_peers.ContainsKey(peerID))
        {
            Peer peer = _peers[peerID];
            var inputFrame = GetInputFrame(peer.LastRemoteTickReceived);
            if (inputFrame != null)
            {
                return inputFrame.GetPlayerInput(peerID);
            }
        }

        return new LocalPeerInputs();
    }

    public NodeInputs GetLatestInputForNode (Node node)
    {
        return GetLatestInputFromPeerForPath(node.GetMultiplayerAuthority(), node.GetPath().ToString());
    }
    
    public NodeInputs GetLatestInputFromPeerForPath (int peerID, string path)
    {
        return GetLatestInputFromPeer(peerID).NodeInputsMap.ContainsKey(path)
            ? GetLatestInputFromPeer(peerID)[path]
            : new NodeInputs();
    }

    public StateBufferFrame GetStateFrame (int tick)
    {
        if (tick < _stateBufferStartTick)
            return null;

        int index = tick - _stateBufferStartTick;
        if (index >= _stateBuffer.Count)
            return null;
        StateBufferFrame stateFrame = _stateBuffer[index];
        Debug.Assert(stateFrame.Tick == tick, "State frame retreived from state buffer has mismatched tick number");
        return stateFrame;
    }

    public bool IsPlayerInputComplete (int tick)
    {
        if (tick > _inputBuffer.Last().Tick)
            return false;

        InputBufferFrame inputFrame = GetInputFrame(tick);
        if (inputFrame == null)
        {
            return true;
        }

        return inputFrame.IsComplete(_peers);
    }

    public bool IsCurrentPlayerInputComplete ()
    {
        return IsPlayerInputComplete(currentTick);
    }
    
    public G.Array<G.Dictionary<string, string>> GetInputMessagesFromSendQueueInRange (int firstIndex, int lastIndex, bool reverse = false)
    {
        var indexes = !reverse ? GD.Range(firstIndex, lastIndex + 1) : GD.Range(lastIndex, firstIndex - 1, -1);
        
        G.Array<G.Dictionary<string, string>> allMessage = new G.Array<G.Dictionary<string, string>>();
        G.Dictionary<string, string> msg = new G.Dictionary<string, string>();
        
        foreach(int index in indexes)
        {
            msg[(_inputSendQueueStartTick + index).ToString()] = _inputSendQueue[index].HexEncode();

            if (MAX_INPUT_FRAMES_PER_MESSAGE > 0 && msg.Count == MAX_INPUT_FRAMES_PER_MESSAGE)
            {
                allMessage.Add(msg);
                msg = new G.Dictionary<string, string>();
            }
        }
        
        if (msg.Count > 0)
            allMessage.Add(msg);

        return allMessage;
    }

    public IEnumerable<G.Dictionary<string, string>> GetInputMessagesFromSendQueueForPeer (Peer peer)
    {
        int firstIndex = peer.NextLocalTickRequested - _inputSendQueueStartTick;
        int lastIndex = _inputSendQueue.Count - 1;
        int maxMessages = (MAX_INPUT_FRAMES_PER_MESSAGE * MAX_MESSAGE_AT_ONCE);

        if ((lastIndex + 1) - firstIndex <= maxMessages)
        {
            return GetInputMessagesFromSendQueueInRange(firstIndex, lastIndex, true);
        }

        var newMessages = (int) Math.Ceiling(MAX_MESSAGE_AT_ONCE / 2.0f);
        var oldMessages = (int) Math.Floor(MAX_MESSAGE_AT_ONCE / 2.0f);

        var a = GetInputMessagesFromSendQueueInRange(lastIndex - (newMessages * MAX_INPUT_FRAMES_PER_MESSAGE) + 1, lastIndex, true);
        var b = GetInputMessagesFromSendQueueInRange(firstIndex, firstIndex + (oldMessages * MAX_INPUT_FRAMES_PER_MESSAGE) - 1);
        
        return a.Concat(b);
    }

    public void RecordAdvantage (bool forceCalculateAdvantage = false)
    {
        float _maxAdvantage = 0;
        foreach (Peer peer in _peers.Values)
        {
            peer.LocalLag = (inputTick + 1) - peer.LastRemoteTickReceived;
            peer.RecordAdvantage(forceCalculateAdvantage ? 0 : TICKS_TO_RECALCULATE_ADVANTAGE);
            _maxAdvantage = Math.Max(_maxAdvantage, peer.CalculatedAdvantage);
        }
    }

    public bool CalculateSkipTicks ()
    {
        float maxAdvantage = 0;
        foreach (Peer peer in _peers.Values)
            maxAdvantage = Math.Max(maxAdvantage, peer.CalculatedAdvantage);

        if (maxAdvantage >= 2.0 && skipTicks == 0)
        {
            skipTicks = (int)(maxAdvantage / 2);
            EmitSignal(nameof(SkipTickFlagged), skipTicks);
            return true;
        }

        return false;
    }

    public int CalculateMessageByte (Dictionary<int, string> msg)
    {
        return msg.SerializeToByteArray().Length;
    }

    public int CalculateMinimumNextTickRequested ()
    {
        if (_peers.Count == 0) return 1;

        int result = _peers.Values.First().NextLocalTickRequested;
        
        foreach (Peer peer in _peers.Values)
            result = Math.Min(result, peer.NextLocalTickRequested);
        
        return result;
    }

    public void SendInputMessageToPeer (int peerID)
    {
        Debug.Assert(peerID != Multiplayer.GetUniqueId(), "Cannot send input to ourselves ");
        Peer peer = _peers[peerID];

        foreach (var input in GetInputMessagesFromSendQueueForPeer(peer))
        {
            var msg = new G.Dictionary<string, string>
            {
                { ((int)InputMessageKey.NEXT_TICK_REQUESTED).ToString(), (peer.LastRemoteTickReceived + 1).ToString() },
                //{ ((int)InputMessageKey.INPUT).ToString(), JSON.Print(input) }
                { ((int)InputMessageKey.INPUT).ToString(), Json.Stringify(input) }
            };

            byte[] bytes = Encoding.ASCII.GetBytes(Json.Stringify(msg));

            if (bytes.Length > DEBUG_MESSAGE_BYTE)
            {
                //GD.PrintErr("Sending message with size: " + bytes.Length + " bytes");
            }
            
            networkAdaptor.SendInputTick(peerID, bytes);
        }
    }

    public void SendInputMessagesToAllPeers ()
    {
        foreach (int peerID in _peers.Keys)
        {
            SendInputMessageToPeer(peerID);
        }
    }

    public override void _PhysicsProcess (double delta)
    {
        if (!started) return;

        if(currentTick == 0)
            SaveCurrentState();

        networkAdaptor.Poll();
        
        if (currentTick > ROLLBACK_DEBUG_TICKS + 1 && ROLLBACK_DEBUG_TICKS > 0)
            rollbackTicks = Math.Max(rollbackTicks, ROLLBACK_DEBUG_TICKS);

        if (INTERPOLATION && currentTick > 1)
        {
            rollbackTicks = Math.Max(rollbackTicks, 1);
        }
        
        if (rollbackTicks > 0)
        {
            int originalTick = currentTick;
            
            Debug.Assert(rollbackTicks + 1 <= _stateBuffer.Count, "Not enough state in buffer to rollback requested number of frames");
            if (rollbackTicks + 1 > _stateBuffer.Count)
            {
                HandleFatalError("Can't rollback " + rollbackTicks + " frames" + " on tick " + currentTick + ", available state: " + _stateBuffer.Count);
                return;
            }
            
            CallLoadState(_stateBuffer[_stateBuffer.Count - (rollbackTicks + 1)].Data);

            List<StateBufferFrame> resized = new List<StateBufferFrame>();
            for (int i = 0; i < _stateBuffer.Count - rollbackTicks; i++)
            {
                resized.Add(_stateBuffer[i]);
            }
            _stateBuffer = resized;

            currentTick -= rollbackTicks;

            EmitSignal(nameof(StateLoaded), rollbackTicks);
          
            while (rollbackTicks > 0)
            {
                currentTick += 1;
                DoTick((float)delta, true);
                rollbackTicks -= 1;
            }
            
            Debug.Assert(currentTick == originalTick, "Rollback didn't return to the original tick");
        }

        if (Multiplayer.IsServer() && _loggedRemoteState.Count > 0)
        {
            ProcessLoggedRemoteState();
        }

        RecordAdvantage();

        if (inputBufferUnderruns < 0)
        {
            inputBufferUnderruns += 1;

            if (inputBufferUnderruns == 0)
            {
                EmitSignal(nameof(SyncRegained));
                skipTicks = 0;
            }
            else
            {
                SendInputMessagesToAllPeers();
                return;
            }
        }   
        else if (!CleanupBuffers())
        {
            if (inputBufferUnderruns == 0)
            {
                EmitSignal(nameof(SyncLost));
            }

            inputBufferUnderruns += 1;
            if (inputBufferUnderruns >= MAX_INPUT_BUFFER_UNDERRUNS)
            {
                HandleFatalError("Unable to regain synchronization");
                return;
            }
            
            SendInputMessagesToAllPeers();
            return;
        }
        else if (inputBufferUnderruns > 0)
        {
            inputBufferUnderruns = -SKIP_TICKS_AFTER_SYNC_REGAINED;
        }
        
        if (skipTicks > 0)
        {
            skipTicks -= 1;
            if (skipTicks == 0)
            {
                foreach(Peer peer in _peers.Values)
                    peer.ClearAdvantage();
            }
            else
            {
                SendInputMessagesToAllPeers();
                return;
            }
        }

        if (CalculateSkipTicks())
            return;

        
        inputTick += 1;
        currentTick += 1;

        var inputFrame = GetOrCreateInputFrame(inputTick);
        if (inputFrame == null) return;
        
        
        var localInput = CallGetLocalInput();
        CalculateInputHash(localInput);
        inputFrame.Players[Multiplayer.GetUniqueId()] = new InputForPlayer(localInput, false);
        //This is what sends the inputs, jesus crist, it took a long time to find it!
        //_inputSendQueue.Add(Encoding.ASCII.GetBytes(JSON.Print(localInput)));
        _inputSendQueue.Add(localInput.Serialize());
        
        Debug.Assert(inputTick == _inputSendQueueStartTick + _inputSendQueue.Count - 1, "Input send queue tick numbers are misaligned");
        SendInputMessagesToAllPeers();

        timeSinceLastTick = 0.0f;

        if (currentTick > 0)
        {
            DoTick((float)delta);

            if (INTERPOLATION)
            {
                var toState = _stateBuffer[_stateBuffer.Count - 1].Data;
                var fromState = _stateBuffer[_stateBuffer.Count - 2].Data;
                interpolationState.Clear();
                foreach (string path in toState.Keys)
                {
                    if (fromState.ContainsKey(path))
                    {
                        interpolationState[path] = new G.Array<G.Dictionary<string, string>>
                            {fromState[path], toState[path]};
                    }
                }
                
                CallLoadState(_stateBuffer[_stateBuffer.Count - 2].Data);
            }
        }
    }
    
    public static byte[] ToByteArray(string HexString)
    {
        int NumberChars = HexString.Length;
        byte[] bytes = new byte[NumberChars / 2];
        for (int i = 0; i < NumberChars; i += 2)
        {
            bytes[i / 2] = Convert.ToByte(HexString.Substring(i, 2), 16);
        }
        return bytes;
    }

    public override void _Process (double delta)
    {
        if (!started) return;

        timeSinceLastTick += (float)delta;
        
        networkAdaptor.Poll();

        if (INTERPOLATION)
        {
            float weight = timeSinceLastTick / _tickTime;
            if (weight > 1.0) weight = 1.0f;
            CallInterpolateFrame(weight);
        }
    }

    public void CalculateInputHash (LocalPeerInputs input)
    {
        LocalPeerInputs cleanedInput = input.Duplicate();
        if (cleanedInput.NodeInputsMap.ContainsKey("$"))
        {
            cleanedInput.NodeInputsMap.Remove("$");
        }

        foreach (string path in cleanedInput.NodeInputsMap.Keys)
        {
            foreach (int key in cleanedInput[path].inputs.Keys)
            {
                var value = cleanedInput[path];
                if (key < 0)
                {
                    value.inputs.Remove(key);
                }
            }
        }
    }

    public void ReceiveInputTick (int peerID, byte[] serializedMsg)
    {
        if (!started) return;

        //TODO man i hope no one read this
        //TODO planned rework: use a class/struct instead of dictionaries
        string raw = Encoding.ASCII.GetString(serializedMsg);
        //GD.Print(raw);
        Dictionary rawAsDictionary = (Dictionary)Json.ParseString(raw);
        G.Dictionary<string, string> msg = new G.Dictionary<string, string>(rawAsDictionary);
        
        G.Dictionary parsing = (Dictionary)Json.ParseString(msg[ ((int)InputMessageKey.INPUT).ToString() ]);
        G.Dictionary<string, string> converted = new G.Dictionary<string, string>(parsing);

        G.Dictionary<int, string> allRemoteInput = new G.Dictionary<int, string>();
        
        foreach (var entry in converted)
        {
            //allRemoteInput[int.Parse(entry.Key)] = entry.Value;
            allRemoteInput.Add(int.Parse(entry.Key), entry.Value);
        }

        int[] allRemoteTicks = allRemoteInput.Keys.ToArray();
        allRemoteTicks = allRemoteTicks.OrderBy(x => x).ToArray();

        var firstRemoteTick = allRemoteTicks[0];
        var lastRemoteTick = allRemoteTicks.Last();
   
        if (firstRemoteTick >= inputTick + MAX_BUFFER_SIZE)
        {
            GD.Print("Discarding message from the future");
            return;
        }
        
        Peer peer = _peers[peerID];

        foreach (var remoteTick in allRemoteTicks)
        {
            
            if (remoteTick <= peer.LastRemoteTickReceived)
            {
                continue;
            }

            if (remoteTick < _inputBufferStartTick)
            {
                continue;
            }

            /*string remoteInputRaw = Encoding.ASCII.GetString(ToByteArray(allRemoteInput[remoteTick])) ;

            var remoteInputUnknown = JSON.Parse(remoteInputRaw).Result as Dictionary;
            G.Dictionary<string, G.Dictionary<int, string>> remoteInput =
                new G.Dictionary<string, G.Dictionary<int, string>>(remoteInputUnknown);
            */

            LocalPeerInputs remoteInput = LocalPeerInputs.Deserialize(ToByteArray(allRemoteInput[remoteTick]));

            var inputFrame = GetOrCreateInputFrame(remoteTick);
            if (inputFrame == null)
            {
                return;
            }

            if (!inputFrame.IsPlayerInputPredicted(peerID))
                continue;

            var tickDelta = currentTick - remoteTick;
            if (tickDelta >= 0 && rollbackTicks <= tickDelta)
            {
                var localInput = inputFrame.GetPlayerInput(peerID);
                inputFrame.Players[peerID] = new InputForPlayer(remoteInput, false);
                
                //if(JSON.Print(localInput) != JSON.Print(remoteInput))
                if(localInput.GetHashCode() != remoteInput.GetHashCode())
                {
                    rollbackTicks = tickDelta + 1;
                    EmitSignal(nameof(RollbackFlagged), remoteTick, peerID, localInput, remoteInput);
                }
            }
            else
            {
                inputFrame.Players[peerID] = new InputForPlayer(remoteInput, false);
            }
        }

        var index = (peer.LastRemoteTickReceived - _inputBufferStartTick) + 1;
        while (index < _inputBuffer.Count && !_inputBuffer[index].IsPlayerInputPredicted(peer.PeerID))
        {
            peer.LastRemoteTickReceived += 1;
            index += 1;
        }

        peer.NextLocalTickRequested = Math.Max(int.Parse(msg[ ((int) InputMessageKey.NEXT_TICK_REQUESTED).ToString()]),
            peer.NextLocalTickRequested);
        peer.RemoteLag = (peer.LastRemoteTickReceived + 1) - peer.NextLocalTickRequested;
    }

    //The master and mastersync rpc behavior is not officially supported anymore. Try using another keyword or making custom logic using Multiplayer.GetRemoteSenderId()
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true)]
    public void LogSavedState (int tick, G.Dictionary<string, G.Dictionary<string, string>> remoteData)
    {
        if (!started) return;
        
        int peerId = Multiplayer.GetRemoteSenderId();
        if (!_loggedRemoteState.ContainsKey(peerId))
        {
            _loggedRemoteState[peerId] = new G.Array<StateBufferFrame>();
        }
        
        _loggedRemoteState[peerId].Add(new StateBufferFrame(tick, remoteData));
    }

    public void ProcessLoggedRemoteState ()
    {
        foreach (int peerId in _loggedRemoteState.Keys)
        {
            G.Array<StateBufferFrame> remoteStateBuffer = _loggedRemoteState[peerId];
            while (remoteStateBuffer.Count > 0)
            {
                int remoteTick = remoteStateBuffer[0].Tick;
                if (!IsPlayerInputComplete(remoteTick))
                    break;

                StateBufferFrame localState = GetStateFrame(remoteTick);
                if (localState == null)
                    break;

                StateBufferFrame remoteState = remoteStateBuffer.First();
                remoteStateBuffer.Remove(remoteState);
                CheckRemoteState(peerId, remoteState, localState);
            }
        }
    }

    public void CheckRemoteState (int peerId, StateBufferFrame remoteState, StateBufferFrame localState)
    {
        if (!localState.Data.Equals(remoteState.Data))
        {
            EmitSignal(nameof(RemoteStateMismatch), localState.Tick, peerId, localState.Data, remoteState.Data);
        }
    }

    public Node Spawn (string name, Node parent, PackedScene scene, Dictionary data = null, bool rename = true,
        string signal = "")
    {
        return SpawnManager.singleton.Spawn(name, parent, scene, data, rename, signal);
    }

    public void OnSpawnManagerSceneSpawned (string name, Node spawnedNode, PackedScene scene, Dictionary data)
    {
        EmitSignal(nameof(SceneSpawned), name, spawnedNode, scene, data);
    }
}
