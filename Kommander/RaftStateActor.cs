﻿
using System.Diagnostics;
using Nixie;
using Kommander.Communication;
using Kommander.Data;
using Kommander.Time;
using Kommander.WAL;

namespace Kommander;

/// <summary>
/// The actor functions as a state machine that allows switching between different
/// states (follower, candidate, leader) and conducting elections without concurrency conflicts.
/// </summary>
public sealed class RaftStateActor : IActorStruct<RaftRequest, RaftResponse>
{
    /// <summary>
    /// Reference to the raft manager
    /// </summary>
    private readonly RaftManager manager;

    /// <summary>
    /// Reference to the raft partition
    /// </summary>
    private readonly RaftPartition partition;
    
    /// <summary>
    /// Reference to the responder actor
    /// </summary>
    private readonly IActorRef<RaftResponderActor, RaftResponderRequest> responderActor;

    /// <summary>
    /// Reference to the WAL actor
    /// </summary>
    private readonly IActorRefStruct<RaftWriteAheadActor, RaftWALRequest, RaftWALResponse> walActor;

    /// <summary>
    /// Track votes per term
    /// </summary>
    private readonly Dictionary<long, HashSet<string>> votes = [];

    /// <summary>
    /// Track the last commit index per node
    /// </summary>
    private readonly Dictionary<string, long> lastCommitIndexes = [];
    
    /// <summary>
    /// Current leader per term
    /// </summary>
    private readonly Dictionary<long, string> expectedLeaders = [];
    
    /// <summary>
    /// 
    /// </summary>
    private readonly Dictionary<HLCTimestamp, RaftProposalQuorum> activeProposals = [];

    /// <summary>
    /// Reference to the logger
    /// </summary>
    private readonly ILogger<IRaft> logger;
    
    /// <summary>
    /// Keep track of slow message processing
    /// </summary>
    private readonly Stopwatch stopwatch = Stopwatch.StartNew();

    /// <summary>
    /// Current node state in the partition
    /// </summary>
    private RaftNodeState nodeState = RaftNodeState.Follower;

    /// <summary>
    /// Current term in the partition
    /// </summary>
    private long currentTerm;

    /// <summary>
    /// Last time the leader sent a heartbeat
    /// </summary>
    private HLCTimestamp lastHeartbeat = HLCTimestamp.Zero;
    
    /// <summary>
    /// Last time we voted
    /// </summary>
    private HLCTimestamp lastVotation = HLCTimestamp.Zero;

    /// <summary>
    /// Time when the voting process started
    /// </summary>
    private HLCTimestamp votingStartedAt = HLCTimestamp.Zero;

    /// <summary>
    /// Timeout to start a new election
    /// </summary>
    private TimeSpan electionTimeout;

    /// <summary>
    /// Whether the WAL is restored or not
    /// </summary>
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
        this.logger = logger;
        
        electionTimeout = TimeSpan.FromMilliseconds(Random.Shared.Next(
            manager.Configuration.StartElectionTimeout, 
            manager.Configuration.EndElectionTimeout
        ));

        responderActor = context.ActorSystem.Spawn<RaftResponderActor, RaftResponderRequest>(
            "raft-responder-" + partition.PartitionId,
            manager,
            partition,
            communication,
            logger
        );
        
        walActor = context.ActorSystem.SpawnStruct<RaftWriteAheadActor, RaftWALRequest, RaftWALResponse>(
            "raft-wal-" + partition.PartitionId, 
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

    /// <summary>
    /// Entry point for the actor
    /// </summary>
    /// <param name="message"></param>
    /// <returns></returns>
    public async Task<RaftResponse> Receive(RaftRequest message)
    {
        stopwatch.Restart();
        
        //Console.WriteLine("[{0}/{1}/{2}] Processing:{3}", manager.LocalEndpoint, partition.PartitionId, state, message.Type);
        //await File.AppendAllTextAsync($"/tmp/{partition.PartitionId}.txt", $"{message.Type}\n");
        
        try
        {
            await RestoreWal().ConfigureAwait(false);

            switch (message.Type)
            {
                case RaftRequestType.CheckLeader:
                    await CheckPartitionLeadership().ConfigureAwait(false);
                    break;

                case RaftRequestType.GetNodeState:
                    return new(RaftResponseType.NodeState, nodeState);

                case RaftRequestType.GetTicketState:
                {
                    (RaftTicketState ticketState, long commitIndex) = CheckTicketCompletion(message.Timestamp);
                    return new(RaftResponseType.TicketState, ticketState, commitIndex);
                }

                case RaftRequestType.AppendLogs:
                    await AppendLogs(message.Endpoint ?? "", message.Term, message.Timestamp, message.Logs).ConfigureAwait(false);
                    break;

                case RaftRequestType.CompleteAppendLogs:
                    await CompleteAppendLogs(message.Endpoint ?? "", message.Timestamp, message.Status, message.CommitIndex).ConfigureAwait(false);
                    break;

                case RaftRequestType.RequestVote:
                    await Vote(new(message.Endpoint ?? ""), message.Term, message.CommitIndex, message.Timestamp).ConfigureAwait(false);
                    break;

                case RaftRequestType.ReceiveVote:
                    await ReceivedVote(message.Endpoint ?? "", message.Term, message.CommitIndex).ConfigureAwait(false);
                    break;

                case RaftRequestType.ReplicateLogs:
                {
                    (RaftOperationStatus status, HLCTimestamp ticketId) = await ReplicateLogs(message.Logs).ConfigureAwait(false);
                    return new(RaftResponseType.None, status, ticketId);
                }

                case RaftRequestType.ReplicateCheckpoint:
                {
                    (RaftOperationStatus status, HLCTimestamp ticketId) = await ReplicateCheckpoint().ConfigureAwait(false);
                    return new(RaftResponseType.None, status, ticketId);
                }

                default:
                    logger.LogError("[{LocalEndpoint}/{PartitionId}/{State}] Invalid message type: {Type}", manager.LocalEndpoint, partition.PartitionId, nodeState, message.Type);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError("[{LocalEndpoint}/{PartitionId}/{State}] {Name} {Message} {StackTrace}", manager.LocalEndpoint, partition.PartitionId, nodeState, ex.GetType().Name, ex.Message, ex.StackTrace);
        }
        finally
        {
            if (stopwatch.ElapsedMilliseconds > manager.Configuration.SlowRaftStateMachineLog)
                logger.LogWarning("[{LocalEndpoint}/{PartitionId}/{State}] Slow message processing: {Type} Elapsed={Elapsed}ms", manager.LocalEndpoint, partition.PartitionId, nodeState,  message.Type, stopwatch.ElapsedMilliseconds);
            
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
        
        logger.LogInformation("[{LocalEndpoint}/{PartitionId}/{State}] WAL restored at #{NextId} in {ElapsedMs}ms", manager.LocalEndpoint, partition.PartitionId, nodeState, currentCommitIndexResponse.Index, stopWatch.ElapsedMilliseconds);
        
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

        switch (nodeState)
        {
            // if node is leader just send hearthbeats every Configuration.HeartbeatInterval
            case RaftNodeState.Leader:
            {
                if (currentTime != HLCTimestamp.Zero && ((currentTime - lastHeartbeat) >= manager.Configuration.HeartbeatInterval))
                    await SendHearthbeat().ConfigureAwait(false);
            
                return;
            }
            
            // Wait Configuration.VotingTimeout seconds after the voting process starts to check if a quorum is available
            case RaftNodeState.Candidate when votingStartedAt != HLCTimestamp.Zero && (currentTime - votingStartedAt) < manager.Configuration.VotingTimeout:
                return;
            
            case RaftNodeState.Candidate:
                logger.LogInformation(
                    "[{LocalEndpoint}/{PartitionId}/{State}] Voting concluded after {Elapsed}ms. No quorum available", 
                    manager.LocalEndpoint, 
                    partition.PartitionId, 
                    nodeState, 
                    (currentTime - votingStartedAt).TotalMilliseconds
                );
            
                nodeState = RaftNodeState.Follower; 
                partition.Leader = "";
                lastHeartbeat = currentTime;
                electionTimeout += TimeSpan.FromMilliseconds(Random.Shared.Next(manager.Configuration.StartElectionTimeoutIncrement, manager.Configuration.EndElectionTimeoutIncrement));
                expectedLeaders.Clear();
                lastCommitIndexes.Clear();
                activeProposals.Clear();
                return;
            
            // if node is follower and leader is not sending hearthbeats, start an election
            case RaftNodeState.Follower when (lastHeartbeat != HLCTimestamp.Zero && ((currentTime - lastHeartbeat) < electionTimeout)):
                return;
            
            case RaftNodeState.Follower:

                // don't start a new election if we recently voted
                if ((lastVotation != HLCTimestamp.Zero && ((currentTime - lastVotation) < (electionTimeout * 2))))
                    return;
                
                partition.Leader = "";
                expectedLeaders.Clear();
                nodeState = RaftNodeState.Candidate;
                votingStartedAt = currentTime;
        
                currentTerm++;
        
                IncreaseVotes(manager.LocalEndpoint, currentTerm);

                logger.LogInformation("[{LocalEndpoint}/{PartitionId}/{State}] Voted to become leader after {LastHeartbeat}ms. Term={CurrentTerm}", manager.LocalEndpoint, partition.PartitionId, nodeState, (currentTime - lastHeartbeat).TotalMilliseconds, currentTerm);

                await RequestVotes(currentTime).ConfigureAwait(false);
                break;
            
            default:
                logger.LogWarning("[{LocalEndpoint}/{PartitionId}/{State}] Unknown node state. Term={CurrentTerm}", manager.LocalEndpoint, partition.PartitionId, nodeState, currentTerm);
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
        List<RaftNode> nodes = manager.Nodes;
        
        if (nodes.Count == 0)
        {
            logger.LogInformation("[{LocalEndpoint}/{PartitionId}/{State}] No other nodes availables to vote", manager.LocalEndpoint, partition.PartitionId, nodeState);
            return;
        }
        
        RaftWALResponse currentMaxLog = await walActor.Ask(new(RaftWALActionType.GetMaxLog)).ConfigureAwait(false);
        
        RequestVotesRequest request = new(partition.PartitionId, currentTerm, currentMaxLog.Index, timestamp, manager.LocalEndpoint);

        foreach (RaftNode node in nodes)
        {
            if (node.Endpoint == manager.LocalEndpoint)
                throw new RaftException("Corrupted nodes");
            
            logger.LogInformation("[{LocalEndpoint}/{PartitionId}/{State}] Asked {Endpoint} for votes on Term={CurrentTerm}", manager.LocalEndpoint, partition.PartitionId, nodeState, node.Endpoint, currentTerm);
            
            responderActor.Send(new(RaftResponderRequestType.RequestVotes, node, request));
        }
    }

    /// <summary>
    /// Sends a heartbeat message to follower nodes to indicate that the leader node in the partition is still alive.
    /// </summary>
    private async Task SendHearthbeat()
    {
        List<RaftNode> nodes = manager.Nodes;
        
        if (nodes.Count == 0)
        {
            logger.LogInformation("[{LocalEndpoint}/{PartitionId}/{State}] No other nodes availables to send hearthbeat", manager.LocalEndpoint, partition.PartitionId, nodeState);
            return;
        }

        lastHeartbeat = await manager.HybridLogicalClock.SendOrLocalEvent().ConfigureAwait(false);;

        if (nodeState != RaftNodeState.Leader && nodeState != RaftNodeState.Candidate)
            return;
        
        foreach (RaftNode node in nodes)
        {
            if (node.Endpoint == manager.LocalEndpoint)
                throw new RaftException("Corrupted nodes");
            
            await AppendLogToNode(node, lastHeartbeat, true);
        }
    }

    /// <summary>
    /// When another node requests our vote, we verify that the term is valid and the commitIndex is
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
            logger.LogInformation("[{LocalEndpoint}/{PartitionId}/{State}] Received request to vote from {Endpoint} but already voted in that Term={Term}. Ignoring...", manager.LocalEndpoint, partition.PartitionId, nodeState, node.Endpoint, voteTerm);
            return;
        }

        if (nodeState != RaftNodeState.Follower && voteTerm == currentTerm)
        {
            logger.LogInformation("[{LocalEndpoint}/{PartitionId}/{State}] Received request to vote from {Endpoint} but we're candidate or leader on the same Term={Term}. Ignoring...", manager.LocalEndpoint, partition.PartitionId, nodeState, node.Endpoint, voteTerm);
            return;
        }

        if (currentTerm > voteTerm)
        {
            logger.LogInformation("[{LocalEndpoint}/{PartitionId}/{State}] Received request to vote on previous term from {Endpoint} Term={Term}. Ignoring...", manager.LocalEndpoint, partition.PartitionId, nodeState, node.Endpoint, voteTerm);
            return;
        }

        string? expectedLeader = expectedLeaders.GetValueOrDefault(voteTerm, "");
        
        if (!string.IsNullOrEmpty(expectedLeader))
        {
            logger.LogInformation("[{LocalEndpoint}/{PartitionId}/{State}] Received request to vote from {Endpoint} but we already voted for {ExpectedLeader}. Ignoring...", manager.LocalEndpoint, partition.PartitionId, nodeState, node.Endpoint, expectedLeader);
            return;
        }
        
        RaftWALResponse localMaxId = await walActor.Ask(new(RaftWALActionType.GetMaxLog)).ConfigureAwait(false);;
        if (localMaxId.Index > remoteMaxLogId)
        {
            logger.LogInformation("[{LocalEndpoint}/{PartitionId}/{State}] Received request to vote on outdated log from {Endpoint} RemoteMaxId={RemoteId} LocalMaxId={MaxId}. Ignoring...", manager.LocalEndpoint, partition.PartitionId, nodeState, node.Endpoint, remoteMaxLogId, localMaxId.Index);
            
            // If we know that we have a commitIndex ahead of other nodes in this partition,
            // we increase the term to force being chosen as leaders.
            currentTerm++;  
            return;
        }
        
        RaftWALResponse maxLogResponse = await walActor.Ask(new(RaftWALActionType.GetMaxLog)).ConfigureAwait(false);

        lastHeartbeat = await manager.HybridLogicalClock.ReceiveEvent(timestamp).ConfigureAwait(false);
        lastVotation = lastHeartbeat;
        
        expectedLeaders.Add(voteTerm, node.Endpoint);

        logger.LogInformation("[{LocalEndpoint}/{PartitionId}/{State}] Sending vote to {Endpoint} on Term={Term}", manager.LocalEndpoint, partition.PartitionId, nodeState, node.Endpoint, voteTerm);

        VoteRequest request = new(partition.PartitionId, voteTerm, maxLogResponse.Index, timestamp, manager.LocalEndpoint);
        
        responderActor.Send(new(RaftResponderRequestType.Vote, node, request));
    }

    /// <summary>
    /// When a node receives a vote from another node, it verifies that the term is valid and that the node
    /// </summary>
    /// <param name="endpoint"></param>
    /// <param name="voteTerm"></param>
    /// <param name="remoteMaxLogId"></param>
    private async Task ReceivedVote(string endpoint, long voteTerm, long remoteMaxLogId)
    {
        if (nodeState == RaftNodeState.Follower)
        {
            logger.LogInformation("[{LocalEndpoint}/{PartitionId}/{State}] Received vote from {Node} but we didn't ask for it Term={Term}. Ignoring...", manager.LocalEndpoint, partition.PartitionId, nodeState, endpoint, voteTerm);
            return;
        }

        if (voteTerm < currentTerm)
        {
            logger.LogWarning("[{LocalEndpoint}/{PartitionId}/{State}] Received vote from {Endpoint} on previous term Term={Term}. Ignoring...", manager.LocalEndpoint, partition.PartitionId, nodeState, endpoint, voteTerm);
            return;
        }
        
        if (nodeState == RaftNodeState.Leader)
        {
            lastCommitIndexes[endpoint] = remoteMaxLogId;
            
            logger.LogInformation("[{LocalEndpoint}/{PartitionId}/{State}] Received vote from {Node} but already declared as leader Term={Term}. Ignoring...", manager.LocalEndpoint, partition.PartitionId, nodeState, endpoint, voteTerm);
            return;
        }
        
        RaftWALResponse maxLogResponse = await walActor.Ask(new(RaftWALActionType.GetMaxLog)).ConfigureAwait(false);

        if (maxLogResponse.Index < remoteMaxLogId)
        {
            logger.LogWarning(
                "[{LocalEndpoint}/{PartitionId}/{State}] Received vote from {Endpoint} but remote node is on a higher RemoteCommitId={CommitId} Local={LocalCommitId}. Ignoring...", 
                manager.LocalEndpoint, 
                partition.PartitionId, 
                nodeState, 
                endpoint, 
                remoteMaxLogId, 
                maxLogResponse.Index
            );
            return;
        }

        int numberVotes = IncreaseVotes(endpoint, voteTerm);
        int quorum = Math.Max(2, (int)Math.Floor((manager.Nodes.Count + 1) / 2f));
        lastCommitIndexes[endpoint] = remoteMaxLogId;
        
        logger.LogInformation(
            "[{LocalEndpoint}/{PartitionId}/{State}] Received vote from {Endpoint} Term={Term} Votes={Votes} Quorum={Quorum}/{Total} RemoteCommitId={CommitId} Local={LocalCommitId}", 
            manager.LocalEndpoint, 
            partition.PartitionId, 
            nodeState, 
            endpoint, 
            voteTerm, 
            numberVotes, 
            quorum, 
            manager.Nodes.Count + 1, 
            remoteMaxLogId, 
            maxLogResponse.Index
        );

        if (numberVotes < quorum)
            return;
        
        nodeState = RaftNodeState.Leader;
        partition.Leader = manager.LocalEndpoint;

        lastHeartbeat = await manager.HybridLogicalClock.SendOrLocalEvent();

        logger.LogInformation(
            "[{LocalEndpoint}/{PartitionId}/{State}] Received vote from {Endpoint} and proclamed leader in {Elapsed}ms Term={Term} Votes={Votes} Quorum={Quorum}/{Total} RemoteCommitId={CommitId} Local={LocalCommitId}", 
            manager.LocalEndpoint, 
            partition.PartitionId, 
            nodeState, 
            endpoint, 
            (lastHeartbeat - votingStartedAt).TotalMilliseconds, 
            voteTerm, 
            numberVotes, 
            quorum, 
            manager.Nodes.Count + 1,
            remoteMaxLogId, 
            maxLogResponse.Index
        );

        await SendHearthbeat();
    }

    /// <summary>
    /// Appends logs to the Write-Ahead Log and updates the state of the node based on the leader's term.
    /// This method usually runs on follower nodes.
    /// </summary>
    /// <param name="endpoint"></param>
    /// <param name="leaderTerm"></param>
    /// <param name="timestamp"></param>
    /// <param name="logs"></param>
    /// <returns></returns>
    private async Task AppendLogs(string endpoint, long leaderTerm, HLCTimestamp timestamp, List<RaftLog>? logs)
    {
        if (currentTerm > leaderTerm)
        {
            logger.LogWarning("[{LocalEndpoint}/{PartitionId}/{State}] Received logs from a leader {Endpoint} with old Term={Term}. Ignoring...", manager.LocalEndpoint, partition.PartitionId, nodeState, endpoint, leaderTerm);
            
            responderActor.Send(new(
                RaftResponderRequestType.CompleteAppendLogs, 
                new(endpoint), 
                new CompleteAppendLogsRequest(partition.PartitionId, leaderTerm, timestamp, manager.LocalEndpoint, RaftOperationStatus.LeaderInOldTerm, -1)
            ));
            return;
        }
        
        //validate if we voted in the current term and we expect a different leader
        string expectedLeader = expectedLeaders.GetValueOrDefault(leaderTerm, "");

        if (endpoint == expectedLeader || string.IsNullOrEmpty(expectedLeader))
        {
            if (partition.Leader != endpoint)
            {
                logger.LogInformation("[{LocalEndpoint}/{PartitionId}/{State}] Leader is now {Endpoint} LeaderTerm={Term}", manager.LocalEndpoint, partition.PartitionId, nodeState, endpoint, leaderTerm);

                partition.Leader = endpoint;
                nodeState = RaftNodeState.Follower;
                currentTerm = leaderTerm;
                lastCommitIndexes.Clear();
                activeProposals.Clear();
                expectedLeaders.TryAdd(leaderTerm, endpoint);
            }
        }
        else
        {
            if (endpoint != expectedLeader)
            {
                logger.LogWarning("[{LocalEndpoint}/{PartitionId}/{State}] Received logs from another leader {Endpoint} (current leader {CurrentLeader}) Term={Term}. Ignoring...", manager.LocalEndpoint, partition.PartitionId, nodeState, endpoint, expectedLeader, leaderTerm);
                
                responderActor.Send(new(
                    RaftResponderRequestType.CompleteAppendLogs, 
                    new(endpoint), 
                    new CompleteAppendLogsRequest(partition.PartitionId, leaderTerm, timestamp, manager.LocalEndpoint, RaftOperationStatus.LeaderInOldTerm, -1)
                ));
                return;
            }
        }

        if (logs is not null && logs.Count > 0)
        {
            logger.LogDebug("[{LocalEndpoint}/{PartitionId}/{State}] Received logs from leader {Endpoint} with Term={Term} Logs={Logs}", manager.LocalEndpoint, partition.PartitionId, nodeState, endpoint, leaderTerm, logs.Count);
            
            lastHeartbeat = await manager.HybridLogicalClock.ReceiveEvent(timestamp).ConfigureAwait(false);

            RaftWALResponse response = await walActor.Ask(new(RaftWALActionType.ProposeOrCommit, leaderTerm, timestamp, logs)).ConfigureAwait(false);
            
            if (response.Status != RaftOperationStatus.Success)
            {
                logger.LogWarning("[{LocalEndpoint}/{PartitionId}/{State}] Couldn't append logs from leader {Endpoint} with Term={Term} Logs={Logs}", manager.LocalEndpoint, partition.PartitionId, nodeState, endpoint, leaderTerm, logs.Count);
                
                responderActor.Send(new(
                    RaftResponderRequestType.CompleteAppendLogs, 
                    new(endpoint), 
                    new CompleteAppendLogsRequest(partition.PartitionId, leaderTerm, timestamp, manager.LocalEndpoint, response.Status, -1)
                ));
                return;
            }
            
            responderActor.Send(new(
                RaftResponderRequestType.CompleteAppendLogs, 
                new(endpoint), 
                new CompleteAppendLogsRequest(partition.PartitionId, leaderTerm, timestamp, manager.LocalEndpoint, RaftOperationStatus.Success, response.Index)
            ));
            return;
        }
        
        lastHeartbeat = await manager.HybridLogicalClock.ReceiveEvent(timestamp).ConfigureAwait(false);
        
        responderActor.Send(new(
            RaftResponderRequestType.CompleteAppendLogs, 
            new(endpoint), 
            new CompleteAppendLogsRequest(partition.PartitionId, leaderTerm, lastHeartbeat, manager.LocalEndpoint, RaftOperationStatus.Success, -1)
        ));
    }

    /// <summary>
    /// Replicates logs to other nodes in the cluster when the node is the leader.
    /// </summary>
    /// <param name="logs"></param>
    /// <returns></returns>
    private async Task<(RaftOperationStatus, HLCTimestamp ticketId)> ReplicateLogs(List<RaftLog>? logs)
    {
        if (logs is null || logs.Count == 0)
            return (RaftOperationStatus.Success, HLCTimestamp.Zero);

        if (nodeState != RaftNodeState.Leader)
            return (RaftOperationStatus.NodeIsNotLeader, HLCTimestamp.Zero);
        
        List<RaftNode> nodes = manager.Nodes;
        
        if (nodes.Count == 0)
        {
            logger.LogWarning("[{LocalEndpoint}/{PartitionId}/{State}] No quorum available to propose logs", manager.LocalEndpoint, partition.PartitionId, nodeState);
            
            return (RaftOperationStatus.Errored, HLCTimestamp.Zero);
        }
        
        HLCTimestamp currentTime = await manager.HybridLogicalClock.SendOrLocalEvent().ConfigureAwait(false);

        foreach (RaftLog log in logs)
        {
            log.Type = RaftLogType.Proposed;
            log.Time = currentTime;
        }

        // Append proposal logs to the Write-Ahead Log
        RaftWALResponse proposeResponse = await walActor.Ask(new(RaftWALActionType.Propose, currentTerm, currentTime, logs)).ConfigureAwait(false);
        if (proposeResponse.Status != RaftOperationStatus.Success)
        {
            logger.LogWarning("[{LocalEndpoint}/{PartitionId}/{State}] Couldn't save proposed logs to local persistence", manager.LocalEndpoint, partition.PartitionId, nodeState);
            
            return (RaftOperationStatus.Errored, HLCTimestamp.Zero);
        }

        RaftProposalQuorum proposalQuorum = new(logs);

        foreach (RaftNode node in nodes)
        {
            if (node.Endpoint == manager.LocalEndpoint)
                throw new RaftException("Corrupted nodes");
            
            proposalQuorum.AddExpectedCompletion(node.Endpoint);
            
            await AppendLogToNode(node, currentTime, false);
        }

        activeProposals.TryAdd(currentTime, proposalQuorum);
        
        logger.LogInformation("[{LocalEndpoint}/{PartitionId}/{State}] Proposed logs {Timestamp} Logs={Logs}", manager.LocalEndpoint, partition.PartitionId, nodeState, currentTime, logs.Count);

        return (RaftOperationStatus.Success, currentTime);
    }

    /// <summary>
    /// Replicates the checkpoint to other nodes in the cluster when the node is the leader.
    /// </summary>
    /// <returns></returns>
    private async Task<(RaftOperationStatus status, HLCTimestamp ticketId)> ReplicateCheckpoint()
    {
        if (nodeState != RaftNodeState.Leader)
            return (RaftOperationStatus.NodeIsNotLeader, HLCTimestamp.Zero);
        
        List<RaftNode> nodes = manager.Nodes;
        
        if (nodes.Count == 0)
        {
            logger.LogWarning("[{LocalEndpoint}/{PartitionId}/{State}] No quorum available to propose logs", manager.LocalEndpoint, partition.PartitionId, nodeState);
            
            return (RaftOperationStatus.Errored, HLCTimestamp.Zero);
        }
        
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
        
        // Append proposal logs to the Write-Ahead Log
        RaftWALResponse proposeResponse = await walActor.Ask(new(RaftWALActionType.Propose, currentTerm, currentTime, checkpointLogs)).ConfigureAwait(false);
        if (proposeResponse.Status != RaftOperationStatus.Success)
        {
            logger.LogWarning("[{LocalEndpoint}/{PartitionId}/{State}] Couldn't save proposed logs to local persistence", manager.LocalEndpoint, partition.PartitionId, nodeState);
            
            return (RaftOperationStatus.Errored, HLCTimestamp.Zero);
        }

        RaftProposalQuorum proposalQuorum = new(checkpointLogs);

        foreach (RaftNode node in nodes)
        {
            if (node.Endpoint == manager.LocalEndpoint)
                throw new RaftException("Corrupted nodes");
            
            proposalQuorum.AddExpectedCompletion(node.Endpoint);
            
            await AppendLogToNode(node, currentTime, false);
        }

        activeProposals.TryAdd(currentTime, proposalQuorum);
        
        logger.LogInformation(
            "[{LocalEndpoint}/{PartitionId}/{State}] Proposed checkpoint logs {Timestamp} Logs={Logs}", 
            manager.LocalEndpoint, 
            partition.PartitionId, 
            nodeState, 
            currentTime, 
            checkpointLogs.Count
        );

        return (RaftOperationStatus.Success, currentTime);
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
    /// Appends logs to a specific node in the cluster.
    /// </summary>
    /// <param name="node"></param>
    /// <param name="timestamp"></param>
    /// <param name="isHearthbeat"></param>
    /// <returns></returns>
    private async Task AppendLogToNode(RaftNode node, HLCTimestamp timestamp, bool isHearthbeat)
    {
        AppendLogsRequest request;

        if (isHearthbeat)
            request = new(partition.PartitionId, currentTerm, timestamp, manager.LocalEndpoint, null);
        else
        {
            long lastCommitIndex = lastCommitIndexes.GetValueOrDefault(node.Endpoint, 0);
            
            lastCommitIndex -= 3;
            if (lastCommitIndex < 0)
                lastCommitIndex = 0;

            RaftWALResponse getRangeResponse = await walActor.Ask(new(RaftWALActionType.GetRange, currentTerm, lastCommitIndex)).ConfigureAwait(false);
            if (getRangeResponse.Logs is null)
                return;

            request = new(partition.PartitionId, currentTerm, timestamp, manager.LocalEndpoint, getRangeResponse.Logs);
        }
        
        responderActor.Send(new(RaftResponderRequestType.AppendLogs, node, request));
    }

    /// <summary>
    /// Called when a node completes an append log operation
    /// </summary>
    /// <param name="endpoint"></param>
    /// <param name="timestamp"></param>
    /// <param name="status"></param>
    /// <param name="committedIndex"></param>
    private async ValueTask CompleteAppendLogs(string endpoint, HLCTimestamp timestamp, RaftOperationStatus status, long committedIndex)
    {
        if (committedIndex > 0)
        {
            lastCommitIndexes[endpoint] = committedIndex;

            logger.LogDebug(
                "[{LocalEndpoint}/{PartitionId}/{State}] Successfully sent logs to {Endpoint} CommitedIndex={Index}",
                manager.LocalEndpoint,
                partition.PartitionId,
                nodeState,
                endpoint,
                committedIndex
            );
        }

        if (status != RaftOperationStatus.Success)
        {
            logger.LogWarning(
                "[{LocalEndpoint}/{PartitionId}/{State}] Got {Status} from {Endpoint} Timestamp={Timestamp}",
                manager.LocalEndpoint,
                partition.PartitionId,
                nodeState,
                status,
                endpoint,
                timestamp
            );

            return;
        }

        if (!activeProposals.TryGetValue(timestamp, out RaftProposalQuorum? proposal))
            return;
        
        proposal.MarkCompleted(endpoint);

        if (!proposal.HasQuorum())
            return;
        
        logger.LogInformation("[{LocalEndpoint}/{PartitionId}/{State}] Proposal completed at {Timestamp}", manager.LocalEndpoint, partition.PartitionId, nodeState, timestamp);
        
        RaftWALResponse commitResponse = await walActor.Ask(new(RaftWALActionType.Commit, currentTerm, timestamp, proposal.Logs)).ConfigureAwait(false);
        if (commitResponse.Status != RaftOperationStatus.Success)
        {
            logger.LogWarning("[{LocalEndpoint}/{PartitionId}/{State}] Couldn't commit logs {Timestamp}", manager.LocalEndpoint, partition.PartitionId, nodeState, timestamp);

            return;
        }

        HLCTimestamp currentTime = await manager.HybridLogicalClock.ReceiveEvent(timestamp);
        
        foreach (string node in proposal.Nodes)
            await AppendLogToNode(new(node), currentTime, false);
        
        logger.LogInformation("[{LocalEndpoint}/{PartitionId}/{State}] Committed logs {Timestamp} Logs={Logs}", manager.LocalEndpoint, partition.PartitionId, nodeState, timestamp, proposal.Logs.Count);
    }
    
    /// <summary>
    /// Checks whether a ticket has been completed or not.
    /// </summary>
    /// <param name="timestamp"></param>
    /// <returns></returns>
    private (RaftTicketState state, long commitIndex) CheckTicketCompletion(HLCTimestamp timestamp)
    {
        if (!activeProposals.TryGetValue(timestamp, out RaftProposalQuorum? proposal))
            return (RaftTicketState.NotFound, -1);

        return proposal.HasQuorum() ? (RaftTicketState.Committed, proposal.LastLogIndex) : (RaftTicketState.Proposed, -1);
    }
}