namespace Draughtsman.Core.Enums;

/// <summary>Who made a change: a person or an AI agent. Audit rows carry this so regulated organisations can filter by it.</summary>
public enum ActorKind
{
    User,
    Agent,
}
