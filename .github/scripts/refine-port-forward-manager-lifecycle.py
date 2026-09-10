from pathlib import Path

path = Path("src/ServerDesk.Application/PortForwarding/IPortForwarding.cs")
text = path.read_text(encoding="utf-8")

text = text.replace("EnterOperation(cancellationToken)", "EnterOperation()")
text = text.replace("EnterOperation(CancellationToken.None)", "EnterOperation()")

old_save = """    {\n        ArgumentNullException.ThrowIfNull(profile);\n        using var operation = EnterOperation();\n        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);"""
new_save = """    {\n        using var operation = EnterOperation();\n        ArgumentNullException.ThrowIfNull(profile);\n        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);"""
if old_save not in text:
    raise SystemExit("Expected SaveProfileAsync validation ordering was not found")
text = text.replace(old_save, new_save, 1)

old_helper = """    private OperationLease EnterOperation(CancellationToken cancellationToken)\n    {\n        cancellationToken.ThrowIfCancellationRequested();\n        lock (_lifecycleSync)"""
new_helper = """    private OperationLease EnterOperation()\n    {\n        lock (_lifecycleSync)"""
if old_helper not in text:
    raise SystemExit("Expected EnterOperation helper was not found")
text = text.replace(old_helper, new_helper, 1)

path.write_text(text, encoding="utf-8")
