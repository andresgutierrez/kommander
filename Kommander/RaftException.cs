﻿
namespace Kommander;

public sealed class RaftException : Exception
{
    public RaftException(string message) : base(message)
    {

    }
}