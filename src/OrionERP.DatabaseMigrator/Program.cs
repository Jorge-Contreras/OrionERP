using OrionERP.DatabaseMigrator;

try
{
  var options = CommandLine.Parse(args);
  using var cancellationSource = new CancellationTokenSource();
  Console.CancelKeyPress += (_, eventArgs) =>
  {
    eventArgs.Cancel = true;
    cancellationSource.Cancel();
  };

  return options.Mode is MigrationMode.Inventory
    ? await SqlInventory.RunAsync(options, cancellationSource.Token)
    : await new MigrationRunner(options).RunAsync(cancellationSource.Token);
}
catch (OperationCanceledException)
{
  Console.Error.WriteLine("Operación cancelada.");
  return 130;
}
catch (Exception exception)
{
  Console.Error.WriteLine(exception.Message);
  Console.Error.WriteLine();
  Console.Error.WriteLine(CommandLine.Usage);
  return 1;
}
