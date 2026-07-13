using Codex.TaskbarStatus.Core;

namespace Codex.TaskbarStatus.Tests;

public sealed class CodexActivityLabelsTests
{
    [Theory]
    [InlineData("Aguardando", "Waiting")]
    [InlineData("Nenhuma execução ativa", "No active execution")]
    [InlineData("Iniciando sessão", "Starting session")]
    [InlineData("Processando solicitação", "Processing request")]
    [InlineData("Aplicando alterações", "Applying changes")]
    [InlineData("Aguardando permissão", "Waiting for permission")]
    [InlineData("Processando resultado", "Processing result")]
    [InlineData("Executando subagente", "Running subagent")]
    [InlineData("Processando resultado do subagente", "Processing subagent result")]
    [InlineData("Executando", "Running")]
    [InlineData("Gerando resposta", "Generating response")]
    [InlineData("Concluído", "Completed")]
    [InlineData("Interrompido", "Interrupted")]
    [InlineData("Erro", "Error")]
    [InlineData("Erro ao aplicar alterações", "Failed to apply changes")]
    [InlineData("Executando ferramenta", "Running tool")]
    [InlineData("Executando functions.shell_command", "Running functions.shell_command")]
    public void ToEnglish_ConvertsLegacyPortugueseActivities(string legacy, string expected)
    {
        Assert.Equal(expected, CodexActivityLabels.ToEnglish(legacy));
    }
}
