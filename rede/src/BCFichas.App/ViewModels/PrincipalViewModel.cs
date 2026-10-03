using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using BCFichas.Core;
using BCFichas.Core.Impressao;
using BCFichas.Core.Pagamento;
using BCFichas.Core.Vendas;

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

        Sistema.Config.Alterada += AoMudarConfig;
        Sistema.Catalogo.Alterado += AoMudarCatalogo;
        Sistema.Vendas.EstoqueAlterado += AoMudarEstoque;

        AplicarConfig(Sistema.Config.Atual);
        AtualizarRelogio();
    }

    public Sistema Sistema { get; }
    public Configuracao Config => Sistema.Config.Atual;

    /// <summary>Senha técnica da aba Máquina (os testes trocam por uma senha conhecida).</summary>
    public SenhaTecnica SenhaTecnica { get; set; } = SenhaTecnica.Padrao;
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
    [ObservableProperty] private string _nomeEvento = "";
    [ObservableProperty] private string _caixaTexto = "";
    [ObservableProperty] private string _operador = "";
    [ObservableProperty] private double _zoom = 1;
    [ObservableProperty] private bool _telaCheia = true;
    [ObservableProperty] private bool _tecladoHabilitado = true;
    [ObservableProperty] private bool _tecladoVisivel;
    [ObservableProperty] private bool _tecladoNumerico;

    /// <summary>
    /// Modo teste: para quem está programando a máquina. As vendas vão para um caixa separado, as fichas
    /// saem marcadas "TESTE" e, ao sair do modo, tudo é apagado (não aparece no relatório do cliente).
    /// </summary>
    [ObservableProperty] private bool _modoTeste;

    public bool TemDialogo => Dialogo is not null;

    public string Suporte => "Suporte " + Configuracao.Suporte;

    partial void OnDialogoChanged(ViewModelBase? value) => OnPropertyChanged(nameof(TemDialogo));

    /// <summary>
    /// Abre o seletor de imagem do sistema (preenchido pela janela). Recebe a pasta onde começar e se deve
    /// avisar quando ela não existe.
    /// </summary>
    public Func<string?, bool, Task<string?>>? EscolherImagem { get; set; }

    /// <summary>
    /// Aplica no Windows o que depende dele (tela sempre ligada, abrir junto com o Windows). Preenchido pelo
    /// programa de verdade; nos testes fica vazio para não mexer no computador.
    /// </summary>
    public Action<Configuracao>? AplicarNoWindows { get; set; }

    /// <summary>Pastas dos pendrives ligados no tablet (os testes trocam por uma pasta qualquer).</summary>
    public Func<IReadOnlyList<string>> Pendrives { get; set; } = PendrivesLigados;

    /// <summary>Escolher um arquivo de programação para abrir (preenchido pela janela). Recebe a pasta inicial.</summary>
    public Func<string?, Task<string?>>? EscolherProgramacao { get; set; }

    /// <summary>Escolher uma pasta (preenchido pela janela). Recebe a pasta em que o seletor abre.</summary>
    public Func<string?, Task<string?>>? EscolherPasta { get; set; }

    private static IReadOnlyList<string> PendrivesLigados()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Removable && d.IsReady)
                .Select(d => d.RootDirectory.FullName)
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Fecha o programa (preenchido pela janela; nos testes não faz nada).</summary>
    public Action? FecharPrograma { get; set; }

    public void Iniciar()
    {
        // Se o programa fechou no meio do modo teste, volta para ele (as vendas de teste continuam separadas).
        var teste = Sistema.Caixa.SessaoTesteAberta(Config.NumeroCaixa);
        ModoTeste = teste is not null;
        Sessao = teste ?? Sistema.Caixa.SessaoAberta(Config.NumeroCaixa);
        AtualizarSessao();
        if (Sessao is null)
            Pagina = new AberturaViewModel(this);
        else
            IrParaVenda(recarregar: true);
        _timerRelogio.Start();
        _ = ResolverPendentesAsync();
    }

    /// <param name="recarregar">
    /// Lê de novo abas e produtos mesmo sem aviso de mudança (ao iniciar). Voltar de uma tela que não mexeu no
    /// cardápio (sangria, relatórios) não refaz os botões.
    /// </param>
    public void IrParaVenda(bool recarregar = false)
    {
        if (Sessao is null)
        {
            Pagina = new AberturaViewModel(this);
            return;
        }
        if (recarregar || _cardapioMudou)
        {
            _cardapioMudou = false;
            Venda.Recarregar();
        }
        Pagina = Venda;
    }

    /// <summary>O cardápio mudou com a venda fora da tela: ela é refeita ao voltar (uma vez, não a cada mudança).</summary>
    private bool _cardapioMudou = true;

    /// <summary>Volta de uma tela aberta pelo menu: a venda com o menu aberto por cima.</summary>
    public void VoltarParaMenu()
    {
        IrParaVenda();
        if (Pagina == Venda) AbrirDialogo(new MenuViewModel(this));
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

    public void PedirSenha(Action aoAcertar, string titulo = "Senha master") =>
        AbrirDialogo(new SenhaViewModel(this, aoAcertar) { Titulo = titulo });

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

    /// <summary>
    /// Desliga o Windows na hora, sem a mensagem "Você escolheu sair" (que aparece quando o desligamento tem
    /// tempo de espera). O banco já está gravado a cada venda, então não se perde nada.
    /// </summary>
    public void DesligarComputador()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                Process.Start(new ProcessStartInfo("shutdown", "/s /f /t 0") { CreateNoWindow = true, UseShellExecute = false });
            }
            catch (Exception e)
            {
                Log.Erro("Desligar", e);
            }
        }
        Sair();
    }

    private int _toquesNaMarca;
    private DateTime _primeiroToque;

    /// <summary>
    /// Atalho escondido do modo teste: tocar 5 vezes seguidas no logo da BC Fichas (ou F1 no teclado).
    /// </summary>
    public void ToqueNaMarca()
    {
        var agora = DateTime.UtcNow;
        if (_toquesNaMarca == 0 || agora - _primeiroToque > TimeSpan.FromSeconds(3))
        {
            _toquesNaMarca = 0;
            _primeiroToque = agora;
        }
        if (++_toquesNaMarca < 5) return;
        _toquesNaMarca = 0;
        PedirModoTeste();
    }

    /// <summary>Liga o modo teste (pedindo a senha master, se houver) ou oferece para desligar.</summary>
    public void PedirModoTeste()
    {
        if (TemDialogo) return;
        if (ModoTeste)
        {
            _ = SairDoModoTeste();
            return;
        }
        if (string.IsNullOrEmpty(Config.SenhaMaster)) _ = EntrarNoModoTeste();
        else PedirSenha(() => _ = EntrarNoModoTeste(), "Modo teste");
    }

    public async Task EntrarNoModoTeste()
    {
        if (ModoTeste) return;
        if (!await Confirmar("Entrar no modo teste?",
                "Para programar e testar a máquina. As vendas feitas no modo teste não entram no relatório do " +
                "cliente e as fichas saem marcadas \"TESTE – SEM VALOR\".\n\nAo sair do modo teste, tudo o que foi " +
                "vendido nele é apagado.", "Entrar no modo teste", "Voltar"))
            return;
        try
        {
            var sessao = Sistema.Caixa.Abrir(Config.NumeroCaixa, null, 0, teste: true);
            ModoTeste = true;
            Sessao = sessao;
            AtualizarSessao();
            Venda.LimparPedido();
            FecharTodosDialogos();
            IrParaVenda();
            MostrarAviso("Modo teste ligado: as vendas não vão para o relatório.");
        }
        catch (ErroDeNegocio e)
        {
            MostrarAviso(e.Message, erro: true);
        }
    }

    public async Task SairDoModoTeste()
    {
        if (!ModoTeste) return;
        if (!await Confirmar("Sair do modo teste?",
                "As vendas, devoluções e sangrias feitas no modo teste serão apagadas e o caixa volta ao normal.",
                "Sair e apagar o teste", "Continuar testando", perigo: true))
            return;
        Sistema.Caixa.ApagarTestes();
        ModoTeste = false;
        Sessao = Sistema.Caixa.SessaoAberta(Config.NumeroCaixa);
        AtualizarSessao();
        Venda.LimparPedido();
        FecharTodosDialogos();
        if (Sessao is null) Pagina = new AberturaViewModel(this);
        else IrParaVenda();
        MostrarAviso("Modo teste desligado. As vendas de teste foram apagadas.");
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
        Sistema.Config.Alterada -= AoMudarConfig;
        Sistema.Catalogo.Alterado -= AoMudarCatalogo;
        Sistema.Vendas.EstoqueAlterado -= AoMudarEstoque;
    }

    private bool _encerrado;

    // Os eventos podem vir de outra thread: repassa para a tela.
    private void AoMudarConfig(Configuracao c) => NaTela(() => AplicarConfig(c));
    private void AoMudarCatalogo() => NaTela(() =>
    {
        if (Pagina == Venda) Venda.Recarregar();
        else _cardapioMudou = true;
    });
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
        AplicarNoWindows?.Invoke(c);
        TecladoHabilitado = c.TecladoNaTela;
        if (!c.TecladoNaTela) TecladoVisivel = false;
    }

    private void AtualizarSessao() => Operador = Sessao?.Operador ?? "";

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
            // Maquininha separada: só o operador sabe se ela aprovou. Pergunta pedido por pedido.
            if (Sistema.Maquininha is MaquininhaSeparada)
            {
                foreach (var pendente in Sistema.Vendas.Pendentes())
                {
                    var aprovou = await Confirmar("Pagamento sem resposta",
                        $"O pedido {pendente.Numero} ({Dinheiro.Formatar(pendente.TotalCentavos)} no " +
                        $"{Nomes.De(pendente.Forma)}) estava esperando a maquininha quando o programa fechou.\n\n" +
                        "A maquininha aprovou esse pagamento?", "Sim, aprovou: imprimir as fichas", "Não aprovou");
                    if (!aprovou)
                    {
                        Sistema.Vendas.Cancelar(pendente.Id);
                        continue;
                    }
                    var pago = Sistema.Vendas.ConfirmarPagamento(pendente.Id, null);
                    var fichas = GeradorFichas.Gerar(pago, Config);
                    var resultado = await ImprimirAsync(() => Sistema.Impressao.Fichas(fichas));
                    if (resultado.Ok) Sistema.Vendas.RegistrarImpressao(pago.Id);
                }
                return;
            }

            var pagos = await Sistema.Vendas.ResolverPendentesAsync(Sistema.Maquininha, CancellationToken.None);
            if (pagos.Count > 0)
                await Mensagem("Pedidos recuperados",
                    $"{pagos.Count} pedido(s) foram pagos antes do programa fechar. Imprima as fichas em Menu > " +
                    (Config.LiberarReimpressao ? "Reimprimir fichas." : "Fichas não impressas."));
        }
        catch (Exception e)
        {
            Log.Erro("Pendentes", e);
        }
    }
}
