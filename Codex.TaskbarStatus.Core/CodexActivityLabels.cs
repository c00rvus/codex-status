namespace Codex.TaskbarStatus.Core;

public static class CodexActivityLabels
{
    public const string Waiting = "Waiting";
    public const string NoActiveExecution = "No active execution";
    public const string StartingSession = "Starting session";
    public const string ProcessingRequest = "Processing request";
    public const string ApplyingChanges = "Applying changes";
    public const string WaitingForPermission = "Waiting for permission";
    public const string WaitingForInput = "Waiting for input";
    public const string ProcessingResult = "Processing result";
    public const string RunningSubagent = "Running subagent";
    public const string ProcessingSubagentResult = "Processing subagent result";
    public const string Running = "Running";
    public const string GeneratingResponse = "Generating response";
    public const string Completed = "Completed";
    public const string Interrupted = "Interrupted";
    public const string Error = "Error";
    public const string FailedToApplyChanges = "Failed to apply changes";

    public static string RunningTool(string toolName) => $"Running {toolName}";

    public static string ToEnglish(string? activity)
    {
        if (string.IsNullOrWhiteSpace(activity))
        {
            return Waiting;
        }

        return activity switch
        {
            "Aguardando" => Waiting,
            "Nenhuma execução ativa" => NoActiveExecution,
            "Iniciando sessão" => StartingSession,
            "Processando solicitação" => ProcessingRequest,
            "Aplicando alterações" => ApplyingChanges,
            "Aguardando permissão" => WaitingForPermission,
            "Processando resultado" => ProcessingResult,
            "Executando subagente" => RunningSubagent,
            "Processando resultado do subagente" => ProcessingSubagentResult,
            "Executando" => Running,
            "Gerando resposta" => GeneratingResponse,
            "Concluído" => Completed,
            "Interrompido" => Interrupted,
            "Erro" => Error,
            "Erro ao aplicar alterações" => FailedToApplyChanges,
            "Executando ferramenta" => RunningTool("tool"),
            _ when activity.StartsWith("Executando ", StringComparison.Ordinal) =>
                $"Running {activity["Executando ".Length..]}",
            _ => activity,
        };
    }
}
