﻿
using System.Diagnostics;
using Nixie;
using Kommander.Communication;
using Kommander.Data;
using Kommander.Time;
using Kommander.WAL;
using Standart.Hash.xxHash;

namespace Kommander;

/// <summary>
/// The actor functions as a state machine that allows switching between different
/// states (follower, candidate, leader) and conducting elections without concurrency conflicts.
/// </summary>
public sealed class RaftStateActor : IActorStruct<RaftRequest, RaftResponse>
{
    private readonly RaftManager manager;

    private readonly RaftPartition partition;

    private readonly ICommunication communication;

    private readonly IActorRefStruct<RaftWriteAheadActor, RaftWALRequest, RaftWALResponse> walActor;

    private readonly Dictionary<long, HashSet<string>> votes = [];

    private readonly Dictionary<string, long> lastCommitIndexes = [];

    private readonly ILogger<IRaft> logger;

    private readonly RaftProposalQuorum proposalQuorum;
    
    private readonly Stopwatch stopwatch = Stopwatch.StartNew();

    private NodeState state = NodeState.Follower;

    private long currentTerm;

    private HLCTimestamp lastHeartbeat = HLCTimestamp.Zero;
    
    private HLCTimestamp lastVotation = HLCTimestamp.Zero;

    private HLCTimestamp votingStartedAt = HLCTimestamp.Zero;

    private TimeSpan electionTimeout;

    private bool restored;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="context"></param>
    /// <param name="manager"></param>
    /// <param name="partition"></param>
    /// <param name="walAdapter"></param>
    /// <param name="communication"></param>
    public RaftStateActor(
        IActorContextStruct<RaftStateActor, RaftRequest, RaftResponse> context, 
        RaftManager manager, 
        RaftPartition partition,
        IWAL walAdapter,
        ICommunication communication,
        ILogger<IRaft> logger
    )
    {
        this.manager = manager;
        this.partition = partition;
        this.communication = communication;
        this.logger = logger;
        
        proposalQuorum = new();

        //int x = GetHashInRange(manager.LocalNodeId, );
        //Console.WriteLine($"ElectionTimeout: {manager.LocalNodeId} {x}");
        
        electionTimeout = TimeSpan.FromMilliseconds(Random.Shared.Next(manager.Configuration.StartElectionTimeout, manager.Configuration.EndElectionTimeout) );
        
        walActor = context.ActorSystem.SpawnStruct<RaftWriteAheadActor, RaftWALRequest, RaftWALResponse>(
            "bra-wal-" + partition.PartitionId, 
            manager, 
            partition,
            walAdapter
        );

        context.ActorSystem.StartPeriodicTimerStruct(
            context.Self,
            "check-election",
            new(RaftRequestType.CheckLeader),
            TimeSpan.FromMilliseconds(500),
            this.manager.Configuration.CheckLeaderInterval
        );
    }
    
    private static int GetHashInRange(string input, int min, int max)
    {
        // Compute the hash value for the string.
        uint hash = xxHash32.ComputeHash(input);
        int range = max - min + 1;
    
        // Map the hash to the desired range.
        return (int)((hash % range) + min);
    }

    /// <summary>
    /// Entry point for the actor
    /// </summary>
    /// <param name="message"></param>
    /// <returns></returns>
    public async Task<RaftResponse> Receive(RaftRequest message)
    {
        stopwatch.Restart();
        
        // Console.WriteLine("[{0}/{1}/{2}] Processing:{3}", manager.LocalEndpoint, partition.PartitionId, state, message.Type);
        //await File.AppendAllTextAsync($"/tmp/{partition.PartitionId}.txt", $"{message.Type}\n");
        
        try
        {
            await RestoreWal().ConfigureAwait(false);

            switch (message.Type)
            {
                case RaftRequestType.CheckLeader:
                    await CheckPartitionLeadership().ConfigureAwait(false);
                    break;

                case RaftRequestType.GetState:
                    return new(RaftResponseType.State, state);

                case RaftRequestType.AppendLogs:
                {
                    (RaftOperationStatus status, long commitLogIndex) = await AppendLogs(message.Endpoint ?? "", message.Term, message.Timestamp, message.Logs).ConfigureAwait(false);
                    return new(RaftResponseType.None, status, commitLogIndex);
                }

                case RaftRequestType.RequestVote:
                    await Vote(new(message.Endpoint ?? ""), message.Term, message.MaxLogId, message.Timestamp).ConfigureAwait(false);
                    break;

                case RaftRequestType.ReceiveVote:
                    await ReceivedVote(message.Endpoint ?? "", message.Term, message.MaxLogId).ConfigureAwait(false);
                    break;

                case RaftRequestType.ReplicateLogs:
                {
                    (RaftOperationStatus status, long commitId) = await ReplicateLogs(message.Logs).ConfigureAwait(false);
                    return new(RaftResponseType.None, status, commitId);
                }

                case RaftRequestType.ReplicateCheckpoint:
                {
                    (RaftOperationStatus status, long commitId) = await ReplicateCheckpoint().ConfigureAwait(false);
                    return new(RaftResponseType.None, status, commitId);
                }

                default:
                    logger.LogError("[{LocalEndpoint}/{PartitionId}/{State}] Invalid message type: {Type}", manager.LocalEndpoint, partition.PartitionId, state, message.Type);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError("[{LocalEndpoint}/{PartitionId}/{State}] {Name} {Message} {StackTrace}", manager.LocalEndpoint, partition.PartitionId, state, ex.GetType().Name, ex.Message, ex.StackTrace);
        }
        finally
        {
            if (stopwatch.ElapsedMilliseconds > manager.Configuration.SlowRaftStateMachineLog)
                logger.LogWarning("[{LocalEndpoint}/{PartitionId}/{State}] Slow message processing: {Type} Elapsed={Elapsed}ms", manager.LocalEndpoint, partition.PartitionId, state,  message.Type, stopwatch.ElapsedMilliseconds);
            
            //await File.AppendAllTextAsync($"/tmp/{partition.PartitionId}.txt", $"{stopwatch.ElapsedMilliseconds} {message.Type}\n");
        }

        return new(RaftResponseType.None);
    }

    /// <summary>
    /// un the entire content of the Write-Ahead Log on the partition to recover the initial state of the node.
    /// This should only be done once during node startup.
    /// </summary>
    private async ValueTask RestoreWal()
    {
        if (restored)
            return;

        restored = true;
        lastHeartbeat = await manager.HybridLogicalClock.SendOrLocalEvent().ConfigureAwait(false);
        Stopwatch stopWatch = Stopwatch.StartNew();

        RaftWALResponse currentCommitIndexResponse = await walActor.Ask(new(RaftWALActionType.Recover)).ConfigureAwait(false);
        
        logger.LogInformation("[{LocalEndpoint}/{PartitionId}/{State}] WAL restored at #{NextId} in {ElapsedMs}ms", manager.LocalEndpoint, partition.PartitionId, state, currentCommitIndexResponse.Index, stopWatch.ElapsedMilliseconds);
        
        RaftWALResponse currentTermResponse = await walActor.Ask(new(RaftWALActionType.GetCurrentTerm)).ConfigureAwait(false);
        currentTerm = currentTermResponse.Index;
    }

    /// <summary>
    /// Periodically, it checks the leadership status of the partition and, based on timeouts,
    /// decides whether to start a new election process.
    /// </summary>
    private async Task CheckPartitionLeadership()
    {
        HLCTimestamp currentTime = await manager.HybridLogicalClock.SendOrLocalEvent().ConfigureAwait(false);
        
        // Console.WriteLine("[{0}/{1}/{2}] TimeHearthbeat:{3} ElectionTimeout:{4}", manager.LocalEndpoint, partition.PartitionId, state, lastHeartbeat != HLCTimestamp.Zero ? ((currentTime - lastHeartbeat)) : TimeSpan.Zero, electionTimeout);

        switch (state)
        {
            // if node is leader just send hearthbeats every Configuration.HeartbeatInterval
            case NodeState.Leader:
            {
                if (currentTime != HLCTimestamp.Zero && ((currentTime - lastHeartbeat) >= manager.Configuration.HeartbeatInterval))
                    await SendHearthbeat(false).ConfigureAwait(false);
            
                return;
            }
            
            // Wait Configuration.VotingTimeout seconds after the voting process starts to check if a quorum is available
            case NodeState.Candidate when votingStartedAt != HLCTimestamp.Zero && (currentTime - votingStartedAt) < manager.Configuration.VotingTimeout:
                return;
            
            case NodeState.Candidate:
                logger.LogInformation("[{LocalEndpoint}/{PartitionId}/{State}] Voting concluded after {Elapsed}ms. No quorum available", manager.LocalEndpoint, partition.PartitionId, state, (currentTime - votingStartedAt));
            
                state = NodeState.Follower; 
                partition.Leader = "";
                lastHeartbeat = currentTime;
                electionTimeout += TimeSpan.FromMilliseconds(Random.Shared.Next(manager.Configuration.StartElectionTimeoutIncrement, manager.Configuration.EndElectionTimeoutIncrement));
                Console.WriteLine("New election timeout {0}", electionTimeout);
                lastCommitIndexes.Clear();
                return;
            
            // if node is follower and leader is not sending hearthbeats, start election
            case NodeState.Follower when (lastHeartbeat != HLCTimestamp.Zero && ((currentTime - lastHeartbeat) < electionTimeout)):
                return;
            
            case NodeState.Follower:

                // don't start a new election if we recently voted
                if ((lastVotation != HLCTimestamp.Zero && ((currentTime - lastVotation) < electionTimeout)))
                    return;
                
                partition.Leader = "";
                state = NodeState.Candidate;
                votingStartedAt = currentTime;
        
                currentTerm++;
        
                IncreaseVotes(manager.LocalEndpoint, currentTerm);

                logger.LogInformation("[{LocalEndpoint}/{PartitionId}/{State}] Voted to become leader after {LastHeartbeat}ms. Term={CurrentTerm}", manager.LocalEndpoint, partition.PartitionId, state, currentTime - lastHeartbeat, currentTerm);

                await RequestVotes(currentTime).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>
    /// Requests votes to obtain leadership when a node becomes a candidate, reaching out to other known nodes in the cluster.
    /// </summary>
    /// <param name="timestamp"></param>
    /// <exception cref="RaftException"></exception>
    private async Task RequestVotes(HLCTimestamp timestamp)
    {
        if (manager.Nodes.Count == 0)
        {
            logger.LogInformation("[{LocalEndpoint}/{PartitionId}/{State}] No other nodes availables to vote", manager.LocalEndpoint, partition.PartitionId, state);
            return;
        }
        
        RaftWALResponse currentMaxLog = await walActor.Ask(new(RaftWALActionType.GetMaxLog)).ConfigureAwait(false);;
        
        List<Task> tasks = new(manager.Nodes.Count);

        RequestVotesRequest request = new(partition.PartitionId, currentTerm, currentMaxLog.Index, timestamp, manager.LocalEndpoint);

        foreach (RaftNode node in manager.Nodes)
        {
            if (node.Endpoint == manager.LocalEndpoint)
                throw new RaftException("Corrupted nodes");
            
            logger.LogInformation("[{LocalEndpoint}/{PartitionId}/{State}] Asked {Endpoint} for votes on Term={CurrentTerm}", manager.LocalEndpoint, partition.PartitionId, state, node.Endpoint, currentTerm);
            
            tasks.Add(communication.RequestVotes(manager, partition, node, request));
        }
        
        await Task.WhenAny(tasks).ConfigureAwait(false);
    }

    /// <summary>
    /// It sends a heartbeat message to the follower nodes to indicate that the leader node in the partition is still alive.
    /// </summary>
    private async Task<bool> SendHearthbeat(bool acknowledged)
    {
        if (manager.Nodes.Count == 0)
        {
            logger.LogInformation("[{LocalEndpoint}/{PartitionId}/{State}] No other nodes availables to send hearthbeat", manager.LocalEndpoint, partition.PartitionId, state);
            return false;
        }

        lastHeartbeat = await manager.HybridLogicalClock.SendOrLocalEvent().ConfigureAwait(false);;

        if (state != NodeState.Leader && state != NodeState.Candidate)
            return false;
        
        if (acknowledged)
            return await SendAcknowledgedHearthbeat(lastHeartbeat).ConfigureAwait(false);
        
        return await SendUnacknowledgedHearthbeat(lastHeartbeat).ConfigureAwait(false);
    }

    /// <summary>
    /// When another node requests our vote, it verifies that the term is valid and that the commitIndex is
    /// higher than ours to ensure we don't elect outdated nodes as leaders. 
    /// </summary>
    /// <param name="node"></param>
    /// <param name="voteTerm"></param>
    /// <param name="remoteMaxLogId"></param>
    /// <param name="timestamp"></param>
    private async Task Vote(RaftNode node, long voteTerm, long remoteMaxLogId, HLCTimestamp timestamp)
    {
        if (votes.ContainsKey(voteTerm))
        {
            logger.LogInformation("[{LocalEndpoint}/{PartitionId}/{State}] Received request to vote from {Endpoint} but already voted in that Term={Term}. Ignoring...", manager.LocalEndpoint, partition.PartitionId, state, node.Endpoint, voteTerm);
            return;
        }

        if (state != NodeState.Follower && voteTerm == currentTerm)
        {
            logger.LogInformation("[{LocalEndpoint}/{PartitionId}/{State}] Received request to vote from {Endpoint} but we're candidate or leader on the same Term={Term}. Ignoring...", manager.LocalEndpoint, partition.PartitionId, state, node.Endpoint, voteTerm);
            return;
        }

        if (currentTerm > voteTerm)
        {
            logger.LogInformation("[{LocalEndpoint}/{PartitionId}/{State}] Received request to vote on previous term from {Endpoint} Term={Term}. Ignoring...", manager.LocalEndpoint, partition.PartitionId, state, node.Endpoint, voteTerm);
            return;
        }
        
        RaftWALResponse localMaxId = await walActor.Ask(new(RaftWALActionType.GetMaxLog)).ConfigureAwait(false);;
        if (localMaxId.Index > remoteMaxLogId)
        {
            logger.LogInformation("[{LocalEndpoint}/{PartitionId}/{State}] Received request to vote on outdated log from {Endpoint} RemoteMaxId={RemoteId} LocalMaxId={MaxId}. Ignoring...", manager.LocalEndpoint, partition.PartitionId, state, node.Endpoint, remoteMaxLogId, localMaxId.Index);
            
            // If we know that we have a commitIndex ahead of other nodes in this partition,
            // we increase the term to force being chosen as leaders.
            currentTerm++;  
            return;
        }
        
        RaftWALResponse maxLogResponse = await walActor.Ask(new(RaftWALActionType.GetMaxLog)).ConfigureAwait(false);

        lastHeartbeat = await manager.HybridLogicalClock.ReceiveEvent(timestamp).ConfigureAwait(false);
        lastVotation = lastHeartbeat;

        logger.LogInformation("[{LocalEndpoint}/{PartitionId}/{State}] Sending vote to {Endpoint} on Term={Term}", manager.LocalEndpoint, partition.PartitionId, state, node.Endpoint, voteTerm);

        VoteRequest request = new(partition.PartitionId, voteTerm, maxLogResponse.Index, timestamp, manager.LocalEndpoint);
        
        await communication.Vote(manager, partition, node, request);
    }

    /// <summary>
    /// When a node receives a vote from another node, it verifies that the term is valid and that the node
    /// </summary>
    /// <param name="endpoint"></param>
    /// <param name="voteTerm"></param>
    /// <param name="remoteMaxLogId"></param>
    private async Task ReceivedVote(string endpoint, long voteTerm, long remoteMaxLogId)
    {
        if (state == NodeState.Leader)
        {
            logger.LogInformation("[{LocalEndpoint}/{PartitionId}/{State}] Received vote from {Node} but already declared as leader Term={Term}. Ignoring...", manager.LocalEndpoint, partition.PartitionId, state, endpoint, voteTerm);
            return;
        }

        if (state == NodeState.Follower)
        {
            logger.LogInformation("[{LocalEndpoint}/{PartitionId}/{State}] Received vote from {Node} but we didn't ask for it Term={Term}. Ignoring...", manager.LocalEndpoint, partition.PartitionId, state, endpoint, voteTerm);
            return;
        }

        if (voteTerm < currentTerm)
        {
            logger.LogWarning("[{LocalEndpoint}/{PartitionId}/{State}] Received vote from {Endpoint} on previous term Term={Term}. Ignoring...", manager.LocalEndpoint, partition.PartitionId, state, endpoint, voteTerm);
            return;
        }
        
        RaftWALResponse maxLogResponse = await walActor.Ask(new(RaftWALActionType.GetMaxLog)).ConfigureAwait(false);

        if (maxLogResponse.Index < remoteMaxLogId)
        {
            logger.LogWarning("[{LocalEndpoint}/{PartitionId}/{State}] Received vote from {Endpoint} but remote node is on a higher CommitId={CommitId} Local={LocalCommitId}. Ignoring...", manager.LocalEndpoint, partition.PartitionId, state, endpoint, remoteMaxLogId, maxLogResponse.Index);
            return;
        }

        int numberVotes = IncreaseVotes(endpoint, voteTerm);
        int quorum = Math.Max(2, (int)Math.Floor((manager.Nodes.Count + 1) / 2f));
        
        logger.LogInformation("[{LocalEndpoint}/{PartitionId}/{State}] Received vote from {Endpoint} Term={Term} Votes={Votes} Quorum={Quorum}/{Total}", manager.LocalEndpoint, partition.PartitionId, state, endpoint, voteTerm, numberVotes, quorum, manager.Nodes.Count + 1);

        if (numberVotes < quorum)
            return;

        if (!await SendHearthbeat(true).ConfigureAwait(false))
        {
            logger.LogWarning("[{LocalEndpoint}/{PartitionId}/{State}] One of the followers didn't acknowlege our leadership Term={Term} Votes={Votes} Quorum={Quorum}/{Total}", manager.LocalEndpoint, partition.PartitionId, state, voteTerm, numberVotes, quorum, manager.Nodes.Count + 1);
            return;
        }

        state = NodeState.Leader;
        partition.Leader = manager.LocalEndpoint;
        
        if (remoteMaxLogId > 0)
            lastCommitIndexes[endpoint] = remoteMaxLogId;
        
        Console.WriteLine("RemoteMaxLogId={0} {1}", endpoint, remoteMaxLogId);

        logger.LogInformation("[{LocalEndpoint}/{PartitionId}/{State}] Proclamed leader Term={Term} Votes={Votes} Quorum={Quorum}/{Total}", manager.LocalEndpoint, partition.PartitionId, state, voteTerm, numberVotes, quorum, manager.Nodes.Count + 1);
    }

    /// <summary>
    /// Appends logs to the Write-Ahead Log and updates the state of the node based on the leader's term.
    /// </summary>
    /// <param name="endpoint"></param>
    /// <param name="leaderTerm"></param>
    /// <param name="timestamp"></param>
    /// <param name="logs"></param>
    /// <returns></returns>
    private async Task<(RaftOperationStatus, long)> AppendLogs(string endpoint, long leaderTerm, HLCTimestamp timestamp, List<RaftLog>? logs)
    {
        // Stopwatch stopwatch = Stopwatch.StartNew();
        
        // Console.WriteLine("[{0}/{1}/{2}] AppendLogs #1 {3}", manager.LocalEndpoint, partition.PartitionId, state, stopwatch.ElapsedMilliseconds);
        
        if (currentTerm > leaderTerm)
        {
            logger.LogWarning("[{LocalEndpoint}/{PartitionId}/{State}] Received logs from a leader {Endpoint} with old Term={Term}. Ignoring...", manager.LocalEndpoint, partition.PartitionId, state, endpoint, leaderTerm);
            
            return (RaftOperationStatus.LeaderInOldTerm, -1);
        }
        
        // Console.WriteLine("[{0}/{1}/{2}] AppendLogs #2 {3}", manager.LocalEndpoint, partition.PartitionId, state, stopwatch.ElapsedMilliseconds);
        
        state = NodeState.Follower;
        currentTerm = leaderTerm;
        lastCommitIndexes.Clear();

        if (partition.Leader != endpoint)
        {
            logger.LogInformation("[{LocalEndpoint}/{PartitionId}/{State}] Leader is now {Endpoint} LeaderTerm={Term}", manager.LocalEndpoint, partition.PartitionId, state, endpoint, leaderTerm);
            
            partition.Leader = endpoint;
        }
        
        // Console.WriteLine("[{0}/{1}/{2}] AppendLogs #3 {3}", manager.LocalEndpoint, partition.PartitionId, state, stopwatch.ElapsedMilliseconds);
        
        if (logs is not null && logs.Count > 0)
        {
            logger.LogDebug("[{LocalEndpoint}/{PartitionId}/{State}] Received logs from leader {Endpoint} with Term={Term} Logs={Logs}", manager.LocalEndpoint, partition.PartitionId, state, endpoint, leaderTerm, logs.Count);

            RaftWALResponse response = await walActor.Ask(new(RaftWALActionType.ProposeOrCommit, leaderTerm, timestamp, logs)).ConfigureAwait(false);
            
            // Console.WriteLine("[{0}/{1}/{2}] AppendLogs #4 {3} {4}", manager.LocalEndpoint, partition.PartitionId, state, response.Status, response.Index);
            
            lastHeartbeat = await manager.HybridLogicalClock.ReceiveEvent(timestamp).ConfigureAwait(false);
            
            return (response.Status, response.Index);
        }
        
        // Console.WriteLine("[{0}/{1}/{2}] AppendLogs #4 {3}", manager.LocalEndpoint, partition.PartitionId, state, stopwatch.ElapsedMilliseconds);
        
        lastHeartbeat = await manager.HybridLogicalClock.ReceiveEvent(timestamp).ConfigureAwait(false);

        return (RaftOperationStatus.Success, -1);
    }

    /// <summary>
    /// Replicates logs to other nodes in the cluster when the node is the leader.
    /// </summary>
    /// <param name="logs"></param>
    /// <returns></returns>
    private async Task<(RaftOperationStatus, long commitId)> ReplicateLogs(List<RaftLog>? logs)
    {
        if (logs is null || logs.Count == 0)
            return (RaftOperationStatus.Success, -1);

        if (state != NodeState.Leader)
            return (RaftOperationStatus.NodeIsNotLeader, -1);

        HLCTimestamp currentTime = await manager.HybridLogicalClock.SendOrLocalEvent().ConfigureAwait(false);

        foreach (RaftLog log in logs)
        {
            log.Type = RaftLogType.Proposed;
            log.Time = currentTime;
        }

        // Append proposal logs to the Write-Ahead Log
        RaftWALResponse proposeResponse = await walActor.Ask(new(RaftWALActionType.Propose, currentTerm, currentTime, logs)).ConfigureAwait(false);
        
        // Replicate logs to other nodes in the cluster
        if (!await ProposeLogsToNodes(currentTime).ConfigureAwait(false))
        {
            logger.LogWarning("[{LocalEndpoint}/{PartitionId}/{State}] No quorum available to commit log {CommitIndex}", manager.LocalEndpoint, partition.PartitionId, state, proposeResponse.Index);
            
            return (RaftOperationStatus.Errored, proposeResponse.Index);
        }

        // Commit logs to the Write-Ahead Log
        RaftWALResponse commitResponse = await walActor.Ask(new(RaftWALActionType.Commit, currentTerm, currentTime, logs)).ConfigureAwait(false);
           
        if (!await CommitLogsToNodes(currentTime).ConfigureAwait(false))
            return (RaftOperationStatus.Errored, commitResponse.Index);
        
        return (RaftOperationStatus.Success, commitResponse.Index);
    }

    /// <summary>
    /// Replicates the checkpoint to other nodes in the cluster when the node is the leader.
    /// </summary>
    /// <returns></returns>
    private async Task<(RaftOperationStatus, long commitId)> ReplicateCheckpoint()
    {
        if (state != NodeState.Leader)
            return (RaftOperationStatus.NodeIsNotLeader, -1);
        
        HLCTimestamp currentTime = await manager.HybridLogicalClock.SendOrLocalEvent().ConfigureAwait(false);
        
        List<RaftLog> checkpointLogs = [new()
        {
            Id = 0,
            Term = currentTerm,
            Type = RaftLogType.ProposedCheckpoint,
            Time = currentTime,
            LogType = "",
            LogData = []
        }];

        RaftWALResponse proposeResponse = await walActor.Ask(new(RaftWALActionType.Propose, currentTerm, currentTime, checkpointLogs)).ConfigureAwait(false);
       
        // Replicate the checkpoint to other nodes in the cluster
        if (!await ProposeLogsToNodes(currentTime).ConfigureAwait(false))
        {
            logger.LogWarning("[{LocalEndpoint}/{PartitionId}/{State}] No quorum available to commit checkpoint log {CommitIndex}", manager.LocalEndpoint, partition.PartitionId, state, proposeResponse.Index);
            
            return (RaftOperationStatus.Errored, proposeResponse.Index);
        }

        // Commit logs to the Write-Ahead Log
        RaftWALResponse commitResponse = await walActor.Ask(new(RaftWALActionType.Commit, currentTerm, currentTime, checkpointLogs)).ConfigureAwait(false);
           
        if (!await CommitLogsToNodes(currentTime).ConfigureAwait(false))
            return (RaftOperationStatus.Errored, commitResponse.Index);
        
        return (RaftOperationStatus.Success, commitResponse.Index);
    }

    /// <summary>
    /// Increases the number of votes for a given term.
    /// </summary>
    /// <param name="endpoint"></param>
    /// <param name="term"></param>
    /// <returns></returns>
    private int IncreaseVotes(string endpoint, long term)
    {
        if (votes.TryGetValue(term, out HashSet<string>? votesPerEndpoint))
            votesPerEndpoint.Add(endpoint);
        else
            votes[term] = [endpoint];;

        return votes[term].Count;
    }
    
    /// <summary>
    /// Proposes logs to the Write-Ahead Log and replicates them to other nodes in the cluster when the node is the leader.
    /// </summary>
    /// <param name="timestamp"></param> 
    /// <returns></returns>
    private async Task<bool> ProposeLogsToNodes(HLCTimestamp timestamp)
    {
        List<RaftNode> nodes = manager.Nodes;
        
        if (nodes.Count == 0)
        {
            logger.LogWarning("[{LocalEndpoint}/{PartitionId}/{State}] No other nodes availables to replicate logs", manager.LocalEndpoint, partition.PartitionId, state);
            return false;
        }
        
        proposalQuorum.Restart(Math.Max(2, (int)Math.Floor((nodes.Count + 1) / 2f)), 2);

        foreach (RaftNode node in nodes)
        {
            if (node.Endpoint == manager.LocalEndpoint)
                throw new RaftException("Corrupted nodes");
            
            _ = AppendLogToNode(node, timestamp, false, proposalQuorum);
        }

        await proposalQuorum.Task.ConfigureAwait(false);

        return proposalQuorum.GotQuorum;
    }
    
    /// <summary>
    /// Appends logs to the Write-Ahead Log and replicates them to other nodes in the cluster when the node is the leader.
    /// </summary>
    /// <param name="timestamp"></param> 
    /// <returns></returns>
    private async Task<bool> CommitLogsToNodes(HLCTimestamp timestamp)
    {
        if (manager.Nodes.Count == 0)
        {
            logger.LogWarning("[{LocalEndpoint}/{PartitionId}/{State}] No other nodes availables to replicate logs", manager.LocalEndpoint, partition.PartitionId, state);
            return false;
        }

        List<RaftNode> nodes = manager.Nodes;

        List<Task<RaftOperationStatus>> tasks = new(nodes.Count);

        foreach (RaftNode node in nodes)
            tasks.Add(AppendLogToNode(node, timestamp, false, null));

        await Task.WhenAny(tasks).ConfigureAwait(false);

        return true;
    }
    
    /// <summary>
    /// Appends logs to the Write-Ahead Log and replicates them to other nodes in the cluster when the node is the leader.
    /// </summary>
    /// <param name="timestamp"></param> 
    /// <returns></returns>
    private async Task<bool> SendUnacknowledgedHearthbeat(HLCTimestamp timestamp)
    {
        List<RaftNode> nodes = manager.Nodes;
        
        if (nodes.Count == 0)
        {
            logger.LogWarning("[{LocalEndpoint}/{PartitionId}/{State}] No other nodes availables to replicate logs", manager.LocalEndpoint, partition.PartitionId, state);
            return false;
        }

        List<Task<RaftOperationStatus>> tasks = new(nodes.Count);

        foreach (RaftNode node in nodes)
        {
            if (node.Endpoint == manager.LocalEndpoint)
                throw new RaftException("Corrupted nodes");
            
            tasks.Add(AppendLogToNode(node, timestamp, true, null));
        }

        await Task.WhenAny(tasks).ConfigureAwait(false);
        
        // Console.WriteLine("[{0}/{1}/{2}] SendUnacknowledgedHearthbeat #1", manager.LocalEndpoint, partition.PartitionId, state);

        return true;
    }
    
    /// <summary>
    /// Appends logs to the Write-Ahead Log and replicates them to other nodes in the cluster when the node is the leader.
    /// </summary>
    /// <param name="timestamp"></param> 
    /// <returns></returns>
    private async Task<bool> SendAcknowledgedHearthbeat(HLCTimestamp timestamp)
    {
        List<RaftNode> nodes = manager.Nodes;
        
        if (nodes.Count == 0)
        {
            logger.LogWarning("[{LocalEndpoint}/{PartitionId}/{State}] No other nodes availables to replicate logs", manager.LocalEndpoint, partition.PartitionId, state);
            return false;
        }

        List<Task<RaftOperationStatus>> tasks = new(nodes.Count);

        foreach (RaftNode node in nodes)
        {
            if (node.Endpoint == manager.LocalEndpoint)
                throw new RaftException("Corrupted nodes");
            
            tasks.Add(AppendLogToNode(node, timestamp, true, null));
        }

        RaftOperationStatus[] responses = await Task.WhenAll(tasks).ConfigureAwait(false);
        
        // Console.WriteLine("[{0}/{1}/{2}] SendAcknowledgedHearthbeat #1", manager.LocalEndpoint, partition.PartitionId, state);
        
        return responses.All(x => x == RaftOperationStatus.Success);
    }
    
    /// <summary>
    /// Appends logs to a specific node in the cluster.
    /// </summary>
    /// <param name="node"></param>
    /// <param name="timestamp"></param>
    /// <param name="isHearthbeat"></param>
    /// <param name="quorum"></param>
    /// <returns></returns>
    private async Task<RaftOperationStatus> AppendLogToNode(RaftNode node, HLCTimestamp timestamp, bool isHearthbeat, RaftProposalQuorum? quorum)
    {
        try
        {
            // Console.WriteLine("[{0}/{1}/{2}] AppendLogToNode #1 {3}", manager.LocalEndpoint, partition.PartitionId, state, node.Endpoint);
            
            AppendLogsRequest request;

            if (isHearthbeat)
                request = new(partition.PartitionId, currentTerm, timestamp, manager.LocalEndpoint, null);
            else
            {
                long lastCommitIndex = lastCommitIndexes.GetValueOrDefault(node.Endpoint, 0);
                
                // Console.WriteLine("{0} {1}", node.Endpoint, lastCommitIndex);

                RaftWALResponse getRangeResponse = await walActor.Ask(new(RaftWALActionType.GetRange, currentTerm, lastCommitIndex)).ConfigureAwait(false);
                if (getRangeResponse.Logs is null)
                    return RaftOperationStatus.Errored;

                request = new(partition.PartitionId, currentTerm, timestamp, manager.LocalEndpoint, getRangeResponse.Logs);
            }
            
            // Console.WriteLine("[{0}/{1}/{2}] AppendLogToNode #2 {3}", manager.LocalEndpoint, partition.PartitionId, state, node.Endpoint);

            AppendLogsResponse response = await communication.AppendLogToNode(manager, partition, node, request).ConfigureAwait(false);
            if (response.CommittedIndex > 0)
            {
                lastCommitIndexes[node.Endpoint] = response.CommittedIndex;

                logger.LogDebug(
                    "[{LocalEndpoint}/{PartitionId}/{State}] Sent logs to {Endpoint} CommitedIndex={Index}",
                    manager.LocalEndpoint, 
                    partition.PartitionId, 
                    state,
                    node.Endpoint, 
                    response.CommittedIndex
                );
            }
            
            // Console.WriteLine("[{0}/{1}/{2}] AppendLogToNode #3 {3}", manager.LocalEndpoint, partition.PartitionId, state, node.Endpoint);
            
            if (response.Status == RaftOperationStatus.Success)
                quorum?.SetCompleted();
            else
            {
                logger.LogWarning(
                    "[{LocalEndpoint}/{PartitionId}/{State}] Got {Status} from {Endpoint}",
                    manager.LocalEndpoint, 
                    partition.PartitionId, 
                    state, 
                    response.Status,
                    node.Endpoint
                );
            }
            
            // Console.WriteLine("[{0}/{1}/{2}] AppendLogToNode #4 {3}", manager.LocalEndpoint, partition.PartitionId, state, node.Endpoint);
            
            return response.Status;
        }
        catch (Exception ex)
        {
            logger.LogError(
                "[{LocalEndpoint}/{PartitionId}/{State}] Error appending logs {Error}",
                manager.LocalEndpoint, 
                partition.PartitionId, 
                state, 
                ex.Message
            );
        }

        return RaftOperationStatus.Errored;
    }
}