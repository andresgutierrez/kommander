
using Nixie;
using Kommander.Communication;
using Kommander.Data;

namespace Kommander;

public sealed class RaftResponderActor : IActor<RaftResponderRequest>
{
    private readonly RaftManager manager;
    
    private readonly RaftPartition partition;

    private readonly ICommunication communication;

    private readonly ILogger<IRaft> logger;
    
    public RaftResponderActor(
        IActorContext<RaftResponderActor, RaftResponderRequest> _,
        RaftManager manager, 
        RaftPartition partition,
        ICommunication communication,
        ILogger<IRaft> logger
    )
    {
        this.manager = manager;
        this.partition = partition;
        this.communication = communication;
        this.logger = logger;
    }

    public async Task Receive(RaftResponderRequest message)
    {
        try
        {
            switch (message.Type)
            {
                case RaftResponderRequestType.Vote:
                    await Vote(message);
                    break;
                
                case RaftResponderRequestType.AppendLogs:
                    await AppendLogs(message);
                    break;
                
                case RaftResponderRequestType.RequestVotes:
                    await RequestVotes(message);
                    break;
                
                case RaftResponderRequestType.CompleteAppendLogs:
                    await CompleteAppendLogs(message);
                    break;
                
                default:
                    Console.WriteLine(message.Type);
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }
    }
    
    private async Task Vote(RaftResponderRequest message)
    {
        if (message.Node is null)
            return;
        
        if (message.VoteRequest is null)
            return;
                
        await communication.Vote(manager, partition, message.Node, message.VoteRequest);
    }

    private async Task RequestVotes(RaftResponderRequest message)
    {
        if (message.Node is null)
            return;

        if (message.RequestVotesRequest is null)
            return;

        await communication.RequestVotes(manager, partition, message.Node, message.RequestVotesRequest);
    }

    private async Task AppendLogs(RaftResponderRequest message)
    {
        if (message.Node is null)
            return;
        
        if (message.AppendLogsRequest is null)
            return;
        
        await communication.AppendLogs(manager, partition, message.Node, message.AppendLogsRequest).ConfigureAwait(false);
    }
    
    private async Task CompleteAppendLogs(RaftResponderRequest message)
    {
        if (message.Node is null)
            return;
        
        if (message.CompleteAppendLogsRequest is null)
            return;
        
        await communication.CompleteAppendLogs(manager, partition, message.Node, message.CompleteAppendLogsRequest).ConfigureAwait(false);
    }
}