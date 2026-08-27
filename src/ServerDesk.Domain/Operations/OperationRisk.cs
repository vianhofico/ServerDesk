namespace ServerDesk.Domain.Operations;

public enum OperationRisk
{
    ReadOnly = 0,
    ElevatedRead = 1,
    Mutating = 2,
    Destructive = 3
}
