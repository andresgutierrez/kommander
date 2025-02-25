﻿
namespace Kommander.Data;

public readonly struct RaftRequest
{
    public RaftRequestType Type { get; }

    public long Term { get; } = -1;
    
    public long MaxLogId { get; } = 0;

    public string? Endpoint { get; } = null; 

    public List<RaftLog>? Logs { get; } = null;

    public RaftRequest(RaftRequestType type, long term = -1, long maxLogId = 0, string? endpoint = null, List<RaftLog>? logs = null)
    {
        Type = type;
        Term = term;
        MaxLogId = maxLogId;
        Endpoint = endpoint;
        Logs = logs;
    }

    public RaftRequest(RaftRequestType type, List<RaftLog> logs)
    {
        Type = type;
        Logs = logs;
    }
}

