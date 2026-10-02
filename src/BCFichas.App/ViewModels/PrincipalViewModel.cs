using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using BCFichas.Core;
using BCFichas.Core.Impressao;

namespace BCFichas.App.ViewModels;

/// <summary>Controla a janela: qual tela está aberta, diálogos por cima, avisos e barra de status.</summary>
public sealed partial class PrincipalViewModel : ViewModelBase, IDisposable
{
    private readonly List<ViewModelBase> _dialogos = new();
    private readonly DispatcherTimer _timerRelogio;
    private readonly DispatcherTimer _timerAviso;

    public PrincipalViewModel(Sistema sistema)
    {
        Sistema = sistema;
        Venda = new VendaViewModel(this);

        _timerRelogio = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) => AtualizarRelogio());
        _timerAviso = new DispatcherTimer(TimeSpan.FromSeconds(4), DispatcherPriority.Normal, (_, _) =>
        {
            _timerAviso!.Stop();
            Aviso = null;
        });

        Sistema.Impressao.Impresso += AoImprimir;
        Sistema.Config.Alterada += AoMudarConfig;
        Sistema.Catalogo.Alterado += AoMudarCatalogo;
        Sistema.Vendas.EstoqueAlterado += AoMudarEstoque;

        AplicarConfig(Sistema.Config.Atual);
        AtualizarRelogio();
        StatusImpressora = Sistema.Impressao.Descricao();
    }

    public Sistema Sistema { get; }
    public Configuracao Config => Sistema.Config.Atual;
    public VendaViewModel Venda { get; }
    public SessaoCaixa? Sessao { get; private set; }

    public string Versao { get; } =
        "Versão " + (typeof(PrincipalViewModel).Assembly.GetName().Version?.ToString(3) ?? "3.0.0");

    [ObservableProperty] private ViewModelBase? _pagina;
    [ObservableProperty] private ViewModelBase? _dialogo;
    [ObservableProperty] private string? _aviso;
    [ObservableProperty] private bool _avisoErro;
    [ObservableProperty] private string _relogio = "";
    [ObservableProperty] private string _data = "";
    [ObservableProperty] private string _statusImpressora = "";
    [ObservableProperty] private bool _impressoraComErro;
    [ObservableProperty] private string _statusMaquininha = "";
    [ObservableProperty] private string _nomeEvento = "";
    [ObservableProperty] private string _caixaTexto = "";
    [ObservableProperty] private string _operador = "";
    [ObservableProperty] private double _zoom = 1;
    [ObservableProperty] private bool _telaCheia = true;
    [ObservableProperty] private bool _tecladoHabilitado = true;
    [ObservableProperty] private bool _tecladoVisivel;
    [ObservableProperty] private bool _tecladoNumerico;

    public bool TemDialogo => Dialogo is not null;

    partial void OnDialogoChanged(ViewModelBase? value) => OnPropertyChanged(nameof(TemDialogo));

    /// <summary>Abre o seletor de imagem do sistema (preenchido pela janela).</summary>
    public Func<Task<string?>>? EscolherImagem { get; set; }

    /// <summary>Fecha o programa (preenchido pela janela; nos testes não faz nada).</summary>
    public Action? FecharPrograma { get; set; }

    public void Iniciar()
    {
        Sessao = Sistema.Caixa.SessaoAberta(Config.NumeroCaixa);
        AtualizarSessao();
        if (Sessao is null)
            Pagina = new AberturaViewModel(this);
        else
            IrParaVenda();
        _timerRelogio.Start();
        _ = ResolverPendentesAsync();
    }

    public void IrParaVenda()
    {
        if (Sessao is null)
        {
            Pagina = new AberturaViewModel(this);
            return;
        }
        Venda.Recarregar();
        Pagina = Venda;
    }

    public void Abrir(PaginaViewModel pagina)
    {
        FecharTodosDialogos();
        pagina.AoAbrir();
        Pagina = pagina;
    }

    /// <summary>Abre uma tela, pedindo a senha master se ela estiver travada.</summary>
    public void AbrirProtegido(TelaProtegida tela, Func<PaginaViewModel> criar)
    {
        if (Config.Protegida(tela))
            PedirSenha(() => Abrir(criar()));
        else
            Abrir(criar());
    }

    public void ExecutarProtegido(TelaProtegida tela, Action acao)
    {
        if (Config.Protegida(tela)) PedirSenha(acao);
        else acao();
    }

    public void PedirSenha(Action aoAcertar) => AbrirDialogo(new SenhaViewModel(this, aoAcertar));

    public void AbrirDialogo(ViewModelBase dialogo)
    {
        _dialogos.Add(dialogo);
        Dialogo = dialogo;
    }

    public void FecharDialogo(ViewModelBase dialogo)
    {
        _dialogos.Remove(dialogo);
        Dialogo = _dialogos.LastOrDefault();
    }

    public void FecharTodosDialogos()
    {
        _dialogos.Clear();
        Dialogo = null;
    }

    public Task<bool> Confirmar(string titulo, string texto, string sim = "Sim", string nao = "Cancelar",
        bool perigo = false)
    {
        var dialogo = new MensagemViewModel(this, titulo, texto, sim, nao, perigo);
        AbrirDialogo(dialogo);
        return dialogo.Resposta;
    }

    public Task Mensagem(string titulo, string texto)
    {
        var dialogo = new MensagemViewModel(this, titulo, texto, "OK", null, false);
        AbrirDialogo(dialogo);
        return dialogo.Resposta;
    }

    /// <summary>Aviso rápido no rodapé da tela (some sozinho).</summary>
    public void MostrarAviso(string texto, bool erro = false)
    {
        Aviso = texto;
        AvisoErro = erro;
        _timerAviso.Stop();
        _timerAviso.Interval = TimeSpan.FromSeconds(erro ? 6 : 3.5);
        _timerAviso.Start();
    }

    public void CaixaAberto(SessaoCaixa sessao)
    {
        Sessao = sessao;
        AtualizarSessao();
        Venda.LimparPedido();
        IrParaVenda();
    }

    public void CaixaFechado()
    {
        Sessao = null;
        AtualizarSessao();
        Venda.LimparPedido();
        FecharTodosDialogos();
        Pagina = new AberturaViewModel(this);
    }

    public void Sair()
    {
        if (FecharPrograma is not null)
        {
            FecharPrograma();
            return;
        }
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    public void DesligarComputador()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                Process.Start(new ProcessStartInfo("shutdown", "/s /t 15") { CreateNoWindow = true, UseShellExecute = false });
            }
            catch (Exception e)
            {
                Log.Erro("Desligar", e);
            }
        }
        Sair();
    }

    /// <summary>Executa a impressão fora da tela (não trava os botões) e avisa se der erro.</summary>
    public async Task<ResultadoImpressao> ImprimirAsync(Func<ResultadoImpressao> imprimir)
    {
        var resultado = await Task.Run(imprimir);
        if (!resultado.Ok) MostrarAviso(resultado.Mensagem, erro: true);
        return resultado;
    }

    /// <summary>Solta os eventos do sistema (usado ao trocar de janela e nos testes).</summary>
    public void Dispose()
    {
        _encerrado = true;
        _timerRelogio.Stop();
        _timerAviso.Stop();
        Sistema.Impressao.Impresso -= AoImprimir;
        Sistema.Config.Alterada -= AoMudarConfig;
        Sistema.Catalogo.Alterado -= AoMudarCatalogo;
        Sistema.Vendas.EstoqueAlterado -= AoMudarEstoque;
    }

    private bool _encerrado;

    // Os eventos podem vir de outra thread (impressão): repassa para a tela.
    private void AoImprimir(ResultadoImpressao r) => NaTela(() => AtualizarImpressora(r));
    private void AoMudarConfig(Configuracao c) => NaTela(() => AplicarConfig(c));
    private void AoMudarCatalogo() => NaTela(Venda.Recarregar);
    private void AoMudarEstoque() => NaTela(Venda.AtualizarEstoque);

    private void NaTela(Action acao) => Dispatcher.UIThread.Post(() =>
    {
        if (!_encerrado) acao();
    });

    private void AplicarConfig(Configuracao c)
    {
        NomeEvento = c.NomeEvento;
        CaixaTexto = $"CAIXA {c.NumeroCaixa:00}";
        Zoom = c.Zoom / 100.0;
        TelaCheia = c.TelaCheia;
        TecladoHabilitado = c.TecladoNaTela;
        if (!c.TecladoNaTela) TecladoVisivel = false;
        StatusMaquininha = "Maquininha: " + Sistema.Maquininha.Nome;
        StatusImpressora = Sistema.Impressao.Descricao();
        ImpressoraComErro = false;
    }

    private void AtualizarSessao() => Operador = Sessao?.Operador ?? "";

    private void AtualizarImpressora(ResultadoImpressao resultado)
    {
        ImpressoraComErro = !resultado.Ok;
        StatusImpressora = resultado.Ok ? Sistema.Impressao.Descricao() : resultado.Mensagem;
    }

    private void AtualizarRelogio()
    {
        var agora = DateTime.Now;
        Relogio = agora.ToString("HH:mm", CultureInfo.InvariantCulture);
        Data = agora.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
    }

    private async Task ResolverPendentesAsync()
    {
        try
        {
            var pagos = await Sistema.Vendas.ResolverPendentesAsync(Sistema.Maquininha, CancellationToken.None);
            if (pagos.Count > 0)
                await Mensagem("Pedidos recuperados",
                    $"{pagos.Count} pedido(s) foram pagos antes do programa fechar. Reimprima as fichas em Menu > Reimprimir fichas.");
        }
        catch (Exception e)
        {
            Log.Erro("Pendentes", e);
        }
    }
}
