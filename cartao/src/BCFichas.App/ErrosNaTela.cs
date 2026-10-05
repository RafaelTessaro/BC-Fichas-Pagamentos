using Avalonia.Threading;
using BCFichas.App.ViewModels;
using BCFichas.Core;

namespace BCFichas.App;

/// <summary>
/// Erro inesperado num botão (ex.: o disco deu erro no meio de uma gravação): em vez de fechar o programa no meio
/// da festa, registra no log, avisa o operador e continua (o que estava sendo gravado não foi concluído). Se os
/// erros se repetem sem parar (a tela não consegue nem se desenhar), deixa o programa fechar, como antes.
/// </summary>
internal sealed class ErrosNaTela : IDisposable
{
    private const int MaximoSeguidos = 5;
    private static readonly TimeSpan Janela = TimeSpan.FromSeconds(10);

    private readonly PrincipalViewModel _principal;
    private readonly Queue<DateTime> _recentes = new();

    private ErrosNaTela(PrincipalViewModel principal)
    {
        _principal = principal;
        Dispatcher.UIThread.UnhandledException += AoErrar;
    }

    public static ErrosNaTela Instalar(PrincipalViewModel principal) => new(principal);

    private void AoErrar(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Erro("Erro na tela", e.Exception);
        var agora = DateTime.UtcNow;
        while (_recentes.Count > 0 && agora - _recentes.Peek() > Janela) _recentes.Dequeue();
        _recentes.Enqueue(agora);
        if (_recentes.Count > MaximoSeguidos) return;

        e.Handled = true;
        _principal.MostrarAviso(e.Exception is ErroDeNegocio negocio
            ? negocio.Message
            : "Algo deu errado e não foi concluído. Tente de novo; se repetir, chame o suporte.", erro: true);
    }

    public void Dispose() => Dispatcher.UIThread.UnhandledException -= AoErrar;
}
