
using Kommander.Communication.Memory;
using Kommander.Data;
using Kommander.Discovery;
using Kommander.Time;
using Kommander.WAL;
using Microsoft.Extensions.Logging;
using Moq;
using Nixie;

namespace Kommander.Tests;

public class TestTwoNodeCluster
{
    private static IRaft GetNode1(InMemoryCommunication communication)
    {
        ActorSystem actorSystem = new();
        
        RaftConfiguration config = new()
        {
            Host = "localhost",
            Port = 8001,
            MaxPartitions = 1
        };
        
        RaftManager node = new(
            actorSystem, 
            config, 
            new StaticDiscovery([new("localhost:8002")]),
            new InMemoryWAL(),
            communication,
            new HybridLogicalClock(),
            new Mock<ILogger<IRaft>>().Object
        );

        return node;
    }
    
    private static IRaft GetNode2(InMemoryCommunication communication)
    {
        ActorSystem actorSystem = new();
        
        RaftConfiguration config = new()
        {
            Host = "localhost",
            Port = 8002,
            MaxPartitions = 1
        };
        
        RaftManager node = new(
            actorSystem, 
            config, 
            new StaticDiscovery([new("localhost:8001")]),
            new InMemoryWAL(),
            communication,
            new HybridLogicalClock(),
            new Mock<ILogger<IRaft>>().Object
        );

        return node;
    }
    
    [Fact]
    public async Task TestJoinCluster()
    {
        InMemoryCommunication communication = new();
        
        IRaft node1 = GetNode1(communication);
        IRaft node2 = GetNode2(communication);

        await node1.JoinCluster();
        await node2.JoinCluster();
        
        node1.ActorSystem.Dispose();
        node2.ActorSystem.Dispose();
    }
    
    [Fact]
    public async Task TestJoinClusterAndDecideLeader()
    {
        InMemoryCommunication communication = new();
        
        IRaft node1 = GetNode1(communication);
        IRaft node2 = GetNode2(communication);

        await node1.JoinCluster();
        await node2.JoinCluster();
        
        Assert.True(node1.Joined);
        Assert.True(node2.Joined);

        await node1.UpdateNodes();
        await node2.UpdateNodes();
        
        communication.SetNodes(new()
        {
            { "localhost:8001", node1 }, 
            { "localhost:8002", node2 }
        });

        while (true)
        {
            if (await node1.AmILeader(0, CancellationToken.None) || await node2.AmILeader(0, CancellationToken.None))
                break;
            
            await Task.Delay(1000);
        }
        
        node1.ActorSystem.Dispose();
        node2.ActorSystem.Dispose();
    }
    
    [Fact]
    public async Task TestJoinClusterSimultAndDecideLeader()
    {
        InMemoryCommunication communication = new();
        
        IRaft node1 = GetNode1(communication);
        IRaft node2 = GetNode2(communication);

        await Task.WhenAll([node1.JoinCluster(), node2.JoinCluster()]);
        
        Assert.True(node1.Joined);
        Assert.True(node2.Joined);

        await node1.UpdateNodes();
        await node2.UpdateNodes();
        
        communication.SetNodes(new()
        {
            { "localhost:8001", node1 }, 
            { "localhost:8002", node2 }
        });

        while (true)
        {
            if (await node1.AmILeader(0, CancellationToken.None) || await node2.AmILeader(0, CancellationToken.None))
                break;
            
            await Task.Delay(1000);
        }
        
        IRaft? leader = await GetLeader([node1, node2]);
        Assert.NotNull(leader);
        
        node1.ActorSystem.Dispose();
        node2.ActorSystem.Dispose();
    }
    
    [Fact]
    public async Task TestJoinClusterSimultAndDecideLeaderWithHighestWal()
    {
        InMemoryCommunication communication = new();
        
        IRaft node1 = GetNode1(communication);
        IRaft node2 = GetNode2(communication);

        await node1.WalAdapter.Propose(0, new() { Id = 1, Term = 1, LogData = "Hello"u8.ToArray(), Time = HLCTimestamp.Zero, Type = RaftLogType.Proposed });
        await node1.WalAdapter.Propose(0, new() { Id = 2, Term = 1, LogData = "Hello"u8.ToArray(), Time = HLCTimestamp.Zero, Type = RaftLogType.Proposed });
        await node1.WalAdapter.Commit(0, new() { Id = 1, Term = 1, LogData = "Hello"u8.ToArray(), Time = HLCTimestamp.Zero, Type = RaftLogType.Committed });
        await node1.WalAdapter.Commit(0, new() { Id = 2, Term = 1, LogData = "Hello"u8.ToArray(), Time = HLCTimestamp.Zero, Type = RaftLogType.Committed });

        await Task.WhenAll([node1.JoinCluster(), node2.JoinCluster()]);
        
        Assert.True(node1.Joined);
        Assert.True(node2.Joined);

        await node1.UpdateNodes();
        await node2.UpdateNodes();
        
        communication.SetNodes(new()
        {
            { "localhost:8001", node1 }, 
            { "localhost:8002", node2 }
        });

        while (true)
        {
            if (await node1.AmILeader(0, CancellationToken.None) || await node2.AmILeader(0, CancellationToken.None))
                break;
            
            await Task.Delay(1000);
        }
        
        IRaft? leader = await GetLeader([node1, node2]);
        Assert.NotNull(leader);
        
        Assert.Equal(node1.GetLocalEndpoint(), leader.GetLocalEndpoint());
        
        List<IRaft> followers = await GetFollowers([node1, node2]);
        Assert.NotEmpty(followers);
        Assert.Single(followers);
        
        long maxNode1 = await node1.WalAdapter.GetMaxLog(0);
        Assert.Equal(2, maxNode1);
        
        long maxNode2 = await node1.WalAdapter.GetMaxLog(0);
        Assert.Equal(2, maxNode2);
        
        node1.ActorSystem.Dispose();
        node2.ActorSystem.Dispose();
    }
    
    [Fact]
    public async Task TestJoinClusterSimultAndDecideLeaderWithHighestTerm()
    {
        InMemoryCommunication communication = new();
        
        IRaft node1 = GetNode1(communication);
        IRaft node2 = GetNode2(communication);
        
        await node1.WalAdapter.Propose(0, new() { Id = 1, Term = 1, LogData = "Hello"u8.ToArray(), Time = HLCTimestamp.Zero, Type = RaftLogType.Proposed });
        await node1.WalAdapter.Propose(0, new() { Id = 2, Term = 1, LogData = "Hello"u8.ToArray(), Time = HLCTimestamp.Zero, Type = RaftLogType.Proposed });
        await node1.WalAdapter.Commit(0, new() { Id = 1, Term = 1, LogData = "Hello"u8.ToArray(), Time = HLCTimestamp.Zero, Type = RaftLogType.Committed });
        await node1.WalAdapter.Commit(0, new() { Id = 2, Term = 1, LogData = "Hello"u8.ToArray(), Time = HLCTimestamp.Zero, Type = RaftLogType.Committed });
        
        await node2.WalAdapter.Propose(0, new() { Id = 1, Term = 2, LogData = "Hello"u8.ToArray(), Time = HLCTimestamp.Zero, Type = RaftLogType.Proposed });
        await node2.WalAdapter.Commit(0, new() { Id = 1, Term = 2, LogData = "Hello"u8.ToArray(), Time = HLCTimestamp.Zero, Type = RaftLogType.Committed });

        await Task.WhenAll([node1.JoinCluster(), node2.JoinCluster()]);
        
        Assert.True(node1.Joined);
        Assert.True(node2.Joined);

        await node1.UpdateNodes();
        await node2.UpdateNodes();
        
        communication.SetNodes(new()
        {
            { "localhost:8001", node1 }, 
            { "localhost:8002", node2 }
        });

        while (true)
        {
            if (await node1.AmILeader(0, CancellationToken.None) || await node2.AmILeader(0, CancellationToken.None))
                break;
            
            await Task.Delay(1000);
        }
        
        IRaft? leader = await GetLeader([node1, node2]);
        Assert.NotNull(leader);
        
        Assert.Equal(node1.GetLocalEndpoint(), leader.GetLocalEndpoint());
        
        List<IRaft> followers = await GetFollowers([node1, node2]);
        Assert.NotEmpty(followers);
        Assert.Single(followers);
        
        long maxNode1 = await node1.WalAdapter.GetMaxLog(0);
        Assert.Equal(2, maxNode1);
        
        long maxNode2 = await node1.WalAdapter.GetMaxLog(0);
        Assert.Equal(2, maxNode2);
        
        node1.ActorSystem.Dispose();
        node2.ActorSystem.Dispose();
    }
    
    private static async Task<IRaft?> GetLeader(IRaft[] nodes)
    {
        foreach (IRaft node in nodes)
        {
            if (await node.AmILeader(0, CancellationToken.None))
                return node;
        }

        return null;
    }
    
    private static async Task<List<IRaft>> GetFollowers(IRaft[] nodes)
    {
        List<IRaft> followers = [];
        
        foreach (IRaft node in nodes)
        {
            if (!await node.AmILeader(0, CancellationToken.None))
                followers.Add(node);
        }

        return followers;
    }
}