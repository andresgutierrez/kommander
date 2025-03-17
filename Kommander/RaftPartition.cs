
using Kommander.Communication;
using Kommander.Data;
using Kommander.Time;
using Kommander.WAL;
using Nixie;

namespace Kommander;

/// <summary>
/// Represents a partition in a Raft cluster.
/// </summary>
public sealed class RaftPartition : IDisposable
{
    private static readonly RaftRequest RaftStateRequest = new(RaftRequestType.GetNodeState);
    
    private readonly SemaphoreSlim semaphore = new(1, 1);

    private readonly IActorRefStruct<RaftStateActor, RaftRequest, RaftResponse> raftActor;

    private readonly RaftManager raftManager;

    internal string Leader { get; set; } = "";

    internal int PartitionId { get; }

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="actorSystem"></param>
    /// <param name="raftManager"></param>
    /// <param name="walAdapter"></param>
    /// <param name="communication"></param>
    /// <param name="partitionId"></param>
    public RaftPartition(
        ActorSystem actorSystem, 
        RaftManager raftManager, 
        IWAL walAdapter, 
        ICommunication communication, 
        int partitionId,
        ILogger<IRaft> logger
    )
    {
        this.raftManager = raftManager;
        
        PartitionId = partitionId;

        raftActor = actorSystem.SpawnStruct<RaftStateActor, RaftRequest, RaftResponse>(
            "raft-partition-" + partitionId, 
            raftManager, 
            this,
            walAdapter,
            communication,
            logger
        );
    }

    /// <summary>
    /// Enqueues a handshake message from the partition.
    /// </summary>
    /// <param name="request"></param>
    public void Handshake(HandshakeRequest request)
    {
        raftActor.Send(new(RaftRequestType.ReceiveHandshake, 0, request.MaxLogId, HLCTimestamp.Zero, request.Endpoint));
    }

    /// <summary>
    /// Enqueues a "request a vote" message from the partition.
    /// </summary>
    /// <param name="request"></param>
    public void RequestVote(RequestVotesRequest request)
    {
        raftActor.Send(new(RaftRequestType.RequestVote, request.Term, request.MaxLogId, request.Time, request.Endpoint));
    }

    /// <summary>
    /// Enqueues a "vote to become leader" message in a partition.
    /// </summary>
    /// <param name="request"></param>
    public void Vote(VoteRequest request)
    {
        raftActor.Send(new(RaftRequestType.ReceiveVote, request.Term, request.MaxLogId, request.Time, request.Endpoint));
    }

    /// <summary>
    /// Append logs to the partition returning the commited index.
    /// </summary>
    /// <param name="request"></param>
    /// <returns></returns>
    public void AppendLogs(AppendLogsRequest request)
    {
        raftActor.Ask(new(
            RaftRequestType.AppendLogs,
            request.Term,
            0,
            request.Time,
            request.Endpoint,
            RaftOperationStatus.Success,
            request.Logs
        ));
    }
    
    /// <summary>
    /// Complete the append logs request
    /// </summary>
    /// <param name="request"></param>
    /// <returns></returns>
    public void CompleteAppendLogs(CompleteAppendLogsRequest request)
    {
        raftActor.Ask(new(
            RaftRequestType.CompleteAppendLogs,
            request.Term,
            request.CommitIndex,
            request.Time,
            request.Endpoint,
            request.Status,
            null
        ));
    }

    /// <summary>
    /// Replicate a single log to the partition.
    /// </summary>
    /// <param name="type"></param>
    /// <param name="data"></param>
    /// <param name="autoCommit"></param>
    /// <returns></returns>
    public async Task<(bool success, RaftOperationStatus status, HLCTimestamp ticketId)> ReplicateLogs(string type, byte[] data, bool autoCommit)
    {
        if (string.IsNullOrEmpty(Leader))
            return (false, RaftOperationStatus.NodeIsNotLeader, HLCTimestamp.Zero);
        
        if (Leader != raftManager.LocalEndpoint)
            return (false, RaftOperationStatus.NodeIsNotLeader, HLCTimestamp.Zero);

        List<RaftLog> logsToReplicate = [new() { Type = RaftLogType.Proposed, LogType = type, LogData = data }];
        
        RaftResponse response = await raftActor.Ask(new(RaftRequestType.ReplicateLogs, logsToReplicate, autoCommit)).ConfigureAwait(false); 
        
        if (response.Status == RaftOperationStatus.Success)
            return (true, response.Status, response.TicketId);
        
        return (false, response.Status, HLCTimestamp.Zero);
    }
    
    /// <summary>
    /// Replicate logs to the partition.
    /// </summary>
    /// <param name="type">A string identifying the user log type</param>
    /// <param name="logs">A list of logs to replicate</param>
    /// <param name="autoCommit"></param>
    /// <returns></returns>
    public async Task<(bool success, RaftOperationStatus status, HLCTimestamp ticketId)> ReplicateLogs(string type, IEnumerable<byte[]> logs, bool autoCommit = true)
    {
        if (string.IsNullOrEmpty(Leader))
            return (false, RaftOperationStatus.NodeIsNotLeader, HLCTimestamp.Zero);
        
        if (Leader != raftManager.LocalEndpoint)
            return (false, RaftOperationStatus.NodeIsNotLeader, HLCTimestamp.Zero);

        List<RaftLog> logsToReplicate = logs.Select(data => new RaftLog { Type = RaftLogType.Proposed, LogType = type, LogData = data }).ToList();
        
        RaftResponse response = await raftActor.Ask(new(RaftRequestType.ReplicateLogs, logsToReplicate, autoCommit)).ConfigureAwait(false);
        
        if (response.Status == RaftOperationStatus.Success)
            return (true, response.Status, response.TicketId);
        
        return (false, response.Status, HLCTimestamp.Zero);
    }
    
    /// <summary>
    /// Commit logs and notify followers in the partition
    /// </summary>
    /// <param name="proposalIndex"></param>
    /// <returns></returns>
    public async Task<(bool success, RaftOperationStatus status, long commitIndex)> CommitLogs(HLCTimestamp ticketId)
    {
        if (string.IsNullOrEmpty(Leader))
            return (false, RaftOperationStatus.NodeIsNotLeader, 0);
        
        if (Leader != raftManager.LocalEndpoint)
            return (false, RaftOperationStatus.NodeIsNotLeader, 0);
        
        RaftResponse response = await raftActor.Ask(new(RaftRequestType.CommitLogs, ticketId, false)).ConfigureAwait(false);
        
        if (response.Status == RaftOperationStatus.Success)
            return (true, response.Status, response.CommitIndex);
        
        return (false, response.Status, 0);
    }

    /// <summary>
    /// Replicate a checkpoint to the partition.
    /// </summary>
    public async Task<(bool success, RaftOperationStatus status, HLCTimestamp ticketId)>  ReplicateCheckpoint()
    {
        if (string.IsNullOrEmpty(Leader))
            return (false, RaftOperationStatus.NodeIsNotLeader, HLCTimestamp.Zero);
        
        if (Leader != raftManager.LocalEndpoint)
            return (false, RaftOperationStatus.NodeIsNotLeader, HLCTimestamp.Zero);
        
        RaftResponse response = await raftActor.Ask(new(RaftRequestType.ReplicateCheckpoint)).ConfigureAwait(false);
        
        if (response.Status == RaftOperationStatus.Success)
            return (true, response.Status, response.TicketId);
        
        return (false, response.Status, HLCTimestamp.Zero);
    }

    /// <summary>
    /// Obtain the state of the partition.
    /// </summary>
    /// <returns></returns>
    /// <exception cref="RaftException"></exception>
    public async ValueTask<RaftNodeState> GetState()
    {
        if (!string.IsNullOrEmpty(Leader) && Leader == raftManager.LocalEndpoint)
            return RaftNodeState.Leader;

        RaftResponse response = await raftActor.Ask(RaftStateRequest, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        
        if (response.Type != RaftResponseType.NodeState)
            throw new RaftException("Unknown response (2)");

        return response.NodeState;
    }
    
    /// <summary>
    /// Obtain the ticket state by the specified ticketId
    /// </summary>
    /// <param name="ticketId"></param>
    /// <returns></returns>
    /// <exception cref="RaftException"></exception>
    public async Task<(RaftTicketState state, long commitId)> GetTicketState(HLCTimestamp ticketId, bool autoCommit)
    {
        RaftResponse response = await raftActor.Ask(new(RaftRequestType.GetTicketState, ticketId, autoCommit), TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        
        if (response.Type != RaftResponseType.TicketState)
            throw new RaftException("Unknown response (2)");

        return (response.TicketState, response.CommitIndex);
    }

    public void Dispose()
    {
        semaphore.Dispose();
    }
}