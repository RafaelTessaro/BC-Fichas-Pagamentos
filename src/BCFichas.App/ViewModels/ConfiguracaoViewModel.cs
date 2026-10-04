using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BCFichas.Core;
using BCFichas.Core.Impressao;
using BCFichas.Core.Servicos;

namespace BCFichas.App.ViewModels;

public sealed record Opcao<T>(T Valor, string Texto)
{
    public override string ToString() => Texto;
}

/// <summary>Uma aba na lista da tela (nova ainda não tem <see cref="Aba"/>). Tudo é gravado ao tocar em Salvar.</summary>
public sealed partial class AbaEdicao : ObservableObject
{
    public AbaEdicao(Aba? aba, int produtos = 0)
    {
        Aba = aba;
        Produtos = produtos;
        _nome = aba?.Nome ?? "";
        _colunas = aba?.Colunas ?? 0;
        _linhas = aba?.Linhas ?? 0;
    }

    public Aba? Aba { get; }
    public bool Nova => Aba is null;
    /// <summary>Produtos na tela de venda desta aba.</summary>
    public int Produtos { get; }
    [ObservableProperty] private string _nome;

    /// <summary>Grade dos botões desta aba: 0 colunas = automática.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Automatica), nameof(GradeTexto), nameof(ProdutosTexto), nameof(NaoCabe))]
    private int _colunas;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GradeTexto), nameof(ProdutosTexto), nameof(NaoCabe))]
    private int _linhas;

    public bool Automatica => Colunas <= 0;
    public int Capacidade => Automatica ? Core.Aba.MaximoAutomatico : Colunas * Math.Max(1, Linhas);
    public string GradeTexto => Automatica ? "Automática" : $"{Colunas} × {Linhas}";
    /// <summary>Mais produtos do que a grade mostra: os de posição maior ficam fora da tela.</summary>
    public bool NaoCabe => Produtos > Capacidade;
    public string ProdutosTexto => (Produtos == 1 ? "1 produto" : $"{Produtos} produtos") +
                                   (NaoCabe ? $" • {Produtos - Capacidade} fora" : "");

    public string NomeLimpo => Nome.Trim().ToUpperInvariant();
    public bool GradeMudou => Aba is not null && (Colunas != Aba.Colunas || Linhas != Aba.Linhas);
    /// <summary>Nova com nome escrito, nome trocado ou grade trocada.</summary>
    public bool Mudou => Aba is null ? NomeLimpo.Length > 0 : NomeLimpo != Aba.Nome || GradeMudou;
}

/// <summary>Uma tela que pode pedir a senha master para abrir.</summary>
public sealed partial class ProtecaoTela(TelaProtegida tela, string nome, string descricao, string icone, bool marcada)
    : ObservableObject
{
    public TelaProtegida Tela { get; } = tela;
    public string Nome { get; } = nome;
    public string Descricao { get; } = descricao;
    public Avalonia.Media.Geometry Icone { get; } = Recursos.Icone(icone);
    [ObservableProperty] private bool _marcada = marcada;
}

/// <summary>Todas as configurações do sistema, separadas em abas.</summary>
public sealed partial class ConfiguracaoViewModel : PaginaViewModel
{
    private readonly DispatcherTimer _timerPrevia;
    private bool _carregando;

    public ConfiguracaoViewModel(PrincipalViewModel principal) : base(principal)
    {
        Abas.CollectionChanged += (_, _) => AtualizarAlteracoes();
        _timerPrevia = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        {
            _timerPrevia!.Stop();
            AtualizarPrevia();
        });
        PropertyChanged += AoMudar;
        MaquinaLiberada = principal.ModoTeste;
    }

    public List<Opcao<ModeloFicha>> Modelos { get; } =
        RenderizadorFicha.Modelos.Select(m => new Opcao<ModeloFicha>(m.Modelo, m.Nome)).ToList();

    public List<Opcao<TipoCorte>> Cortes { get; } =
    [
        new(TipoCorte.Parcial, "Corte parcial (a ficha fica presa por um ponto)"),
        new(TipoCorte.Total, "Corte total (a ficha cai solta)"),
        new(TipoCorte.Nenhum, "Sem guilhotina (rasgar na serrilha)"),
    ];

    public List<Opcao<TipoImpressora>> TiposImpressora { get; } =
    [
        new(Core.TipoImpressora.Windows, "Impressora instalada no Windows (recomendado)"),
        new(Core.TipoImpressora.Serial, "Porta COM (USB-serial)"),
        new(Core.TipoImpressora.Arquivo, "Salvar em arquivo (testar sem impressora)"),
    ];

    public const int MaximoCaixas = 30;
    public List<int> Papeis { get; } = [80, 58];
    public List<int> Velocidades { get; } = [9600, 19200, 38400, 57600, 115200];
    public List<int> Zooms { get; } = [70, 80, 90, 100, 110, 125, 150];
    public List<int> SegundosSimulador { get; } = [0, 2, 3, 5, 10];

    public List<Opcao<TipoMaquininha>> TiposMaquininha { get; } =
    [
        new(Core.TipoMaquininha.Separada, "Maquininha separada: só registrar a forma de pagamento"),
        new(Core.TipoMaquininha.Simulador, "Simulador (para treinar, sem maquininha)"),
    ];
    public ObservableCollection<string> Fontes { get; } = new();
    public ObservableCollection<string> Impressoras { get; } = new();
    public ObservableCollection<string> Portas { get; } = new();
    public ObservableCollection<AbaEdicao> Abas { get; } = new();
    public List<ProtecaoTela> Protecoes { get; private set; } = new();

    [ObservableProperty] private int _abaSelecionada;

    // Geral
    [ObservableProperty] private string _nomeEvento = "";
    [ObservableProperty] private string _rodape = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NumeroCaixaTexto), nameof(CaixaNaFicha))]
    [NotifyCanExecuteChangedFor(nameof(CaixaAnteriorCommand), nameof(ProximoCaixaCommand))]
    private int _numeroCaixa = 1;
    [ObservableProperty] private bool _desligarAoFechar;
    [ObservableProperty] private bool _manterTelaLigada;
    [ObservableProperty] private bool _iniciarComWindows;
    [ObservableProperty] private bool _telaCheia;
    [ObservableProperty] private bool _tecladoNaTela;
    [ObservableProperty] private int _zoom = 100;

    // Ficha
    [ObservableProperty] private Opcao<ModeloFicha>? _modelo;
    [ObservableProperty] private string? _fonte;
    [ObservableProperty] private bool _codigoDeBarras;
    [ObservableProperty] private bool _mostrarValor;
    [ObservableProperty] private bool _moldura;
    [ObservableProperty] private string? _logo;
    [ObservableProperty] private Bitmap? _previa;

    // Botões
    [ObservableProperty] private string _pastaFotos = "";

    // Impressora
    [ObservableProperty] private Opcao<TipoImpressora>? _tipoImpressora;
    [ObservableProperty] private string? _nomeImpressora;
    [ObservableProperty] private string? _portaSerial;
    [ObservableProperty] private int _baudRate = 115200;
    [ObservableProperty] private int _larguraPapel = 80;
    [ObservableProperty] private Opcao<TipoCorte>? _corte;
    [ObservableProperty] private bool _imprimindoTeste;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AjusteTexto))]
    private int _ajusteHorizontal;

    // Maquininha
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaquininhaSimulador), nameof(MaquininhaSeparada))]
    private Opcao<TipoMaquininha>? _tipoMaquininha;
    [ObservableProperty] private int _simuladorSegundos;

    // Segurança
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TemSenha), nameof(SemSenha))]
    private string _senhaMaster = "";

    // Máquina: opções que a BC Fichas libera quando o cliente pede
    [ObservableProperty] private bool _liberarDevolucao;
    [ObservableProperty] private bool _liberarReimpressao;

    public bool TemLogo => Logo is not null;
    public bool ImpressoraWindows => TipoImpressora?.Valor == Core.TipoImpressora.Windows;
    public bool ImpressoraSerial => TipoImpressora?.Valor == Core.TipoImpressora.Serial;
    public bool ImpressoraArquivo => TipoImpressora?.Valor == Core.TipoImpressora.Arquivo;
    public string PastaArquivo => Sistema.Impressao.PastaPadraoArquivo;
    public string PastaDados => Sistema.PastaDados;
    public bool SemImpressoras => Impressoras.Count == 0;
    public bool MaquininhaSimulador => TipoMaquininha?.Valor == Core.TipoMaquininha.Simulador;
    public bool MaquininhaSeparada => !MaquininhaSimulador;
    public bool TemSenha => SenhaMaster.Length > 0;
    public bool SemSenha => !TemSenha;
    public string AjusteTexto => AjusteHorizontal == 0
        ? "Centralizada"
        : $"{Math.Abs(AjusteHorizontal) / 8.0:0.0} mm para a {(AjusteHorizontal < 0 ? "esquerda" : "direita")}".Replace('.', ',');

    // Máquina
    [ObservableProperty] private string _pastaBackup = "";
    [ObservableProperty] private string _situacaoMaquina = "";
    [ObservableProperty] private string _situacaoVendas = "";
    [ObservableProperty] private bool _maquinaPura;
    [ObservableProperty] private bool _ocupado;

    // ---------- Alterações não salvas ----------

    /// <summary>Algo mudou nesta tela e ainda não foi gravado (o botão Salvar fica em destaque).</summary>
    [ObservableProperty] private bool _temAlteracoes;

    /// <summary>Como a tela estava ao abrir (ou no último Salvar), para saber o que mudou.</summary>
    private Configuracao? _salva;
    private List<long> _ordemSalva = new();
    private readonly List<AbaEdicao> _abasExcluidas = new();

    private bool AbasMudaram => _abasExcluidas.Count > 0 || Abas.Any(a => a.Mudou) ||
                                !Abas.Where(a => !a.Nova).Select(a => a.Aba!.Id).SequenceEqual(_ordemSalva);

    private static string Json(Configuracao c) => System.Text.Json.JsonSerializer.Serialize(c);

    private void AtualizarAlteracoes()
    {
        if (_carregando || _salva is null) return;
        TemAlteracoes = Json(Montar()) != Json(_salva) || AbasMudaram;
    }

    /// <summary>A partir de agora o que está na tela é o que está gravado.</summary>
    private void MarcarComoSalva()
    {
        _salva = Montar();
        _ordemSalva = Abas.Where(a => !a.Nova).Select(a => a.Aba!.Id).ToList();
        _abasExcluidas.Clear();
        TemAlteracoes = false;
    }

    /// <summary>Volta tudo para o que está gravado.</summary>
    [RelayCommand]
    private async Task Desfazer()
    {
        if (!await Principal.Confirmar("Desfazer as alterações?",
                "Tudo nesta tela volta a ser como está gravado.", "Desfazer", "Voltar"))
            return;
        AoAbrir();
        Principal.MostrarAviso("Alterações desfeitas.");
    }

    /// <summary>Sair com alterações não salvas: pergunta antes (é fácil esquecer de tocar em Salvar).</summary>
    protected override void Voltar() => _ = VoltarAsync();

    private async Task VoltarAsync()
    {
        if (TemAlteracoes)
        {
            if (await Principal.Confirmar("Salvar as alterações?",
                    "Você mudou as configurações e ainda não tocou em Salvar. Se sair sem salvar, as mudanças se perdem.",
                    "Salvar", "Sair sem salvar"))
            {
                if (!Gravar(out var mudouCaixa)) return;
                Principal.MostrarAviso("Configurações salvas.");
                if (mudouCaixa)
                {
                    Principal.Iniciar();
                    return;
                }
            }
        }
        base.Voltar();
    }

    public override void AoAbrir()
    {
        AtualizarSituacao();
        _carregando = true;
        var c = Principal.Config;

        NomeEvento = c.NomeEvento;
        Rodape = c.Rodape;
        NumeroCaixa = c.NumeroCaixa;
        DesligarAoFechar = c.DesligarAoFechar;
        ManterTelaLigada = c.ManterTelaLigada;
        IniciarComWindows = c.IniciarComWindows;
        TelaCheia = c.TelaCheia;
        TecladoNaTela = c.TecladoNaTela;
        Zoom = Zooms.Contains(c.Zoom) ? c.Zoom : 100;

        Fontes.Clear();
        foreach (var f in Core.Impressao.Fontes.Disponiveis()) Fontes.Add(f);
        if (!Fontes.Contains(c.Fonte)) Fontes.Insert(0, c.Fonte);
        Modelo = Modelos.FirstOrDefault(m => m.Valor == c.Modelo) ?? Modelos[0];
        Moldura = c.Moldura;
        Fonte = c.Fonte;
        CodigoDeBarras = c.CodigoDeBarras;
        MostrarValor = c.MostrarValorNaFicha;
        Logo = c.Logo;

        PastaFotos = c.PastaFotos;
        PastaBackup = c.PastaBackup;
        CarregarAbas();

        ProcurarImpressoras(c.NomeImpressora, c.PortaSerial);
        TipoImpressora = TiposImpressora.First(t => t.Valor == c.Impressora);
        BaudRate = c.BaudRate;
        LarguraPapel = c.LarguraPapelMm <= 58 ? 58 : 80;
        Corte = Cortes.First(x => x.Valor == c.Corte);
        AjusteHorizontal = c.AjusteHorizontal;

        TipoMaquininha = TiposMaquininha.FirstOrDefault(t => t.Valor == c.Maquininha) ?? TiposMaquininha[0];
        SimuladorSegundos = SegundosSimulador.Contains(c.SimuladorAprovarEmSegundos) ? c.SimuladorAprovarEmSegundos : 0;

        SenhaMaster = c.SenhaMaster;
        LiberarDevolucao = c.LiberarDevolucao;
        LiberarReimpressao = c.LiberarReimpressao;
        ProtecaoTela Protecao(TelaProtegida tela, string nome, string descricao, string icone) =>
            new(tela, nome, descricao, icone, c.TelasProtegidas.HasFlag(tela));
        Protecoes =
        [
            Protecao(TelaProtegida.Configuracao, "Configurações", "Evento, ficha, impressora", "IconeConfig"),
            Protecao(TelaProtegida.Produtos, "Produtos", "Cadastrar e mudar preços", "IconeCaixaProduto"),
            Protecao(TelaProtegida.Relatorios, "Relatórios", "Ver quanto vendeu", "IconeGrafico"),
            Protecao(TelaProtegida.Devolucao, "Devolver fichas", "Tira dinheiro do caixa", "IconeTroca"),
            Protecao(TelaProtegida.Sangria, "Sangria / Suprimento", "Tirar ou pôr dinheiro", "IconeDinheiro"),
            Protecao(TelaProtegida.Reimpressao, "Reimprimir fichas", "Segunda via de pedido", "IconeImpressora"),
            Protecao(TelaProtegida.FecharCaixa, "Fechar caixa", "Encerrar o dia", "IconeCadeado"),
            Protecao(TelaProtegida.SairDoPrograma, "Sair do programa", "Fechar o BC Fichas", "IconeDesligar"),
        ];
        foreach (var p in Protecoes) p.PropertyChanged += (_, _) => AtualizarAlteracoes();
        OnPropertyChanged(nameof(Protecoes));
        OnPropertyChanged(nameof(SemImpressoras));

        _carregando = false;
        MarcarComoSalva();
        AtualizarPrevia();
    }

    [RelayCommand]
    private void Salvar()
    {
        if (!Gravar(out var mudouCaixa)) return;
        Principal.MostrarAviso("Configurações salvas.");
        if (mudouCaixa) Principal.Iniciar();
    }

    /// <summary>Confere e grava o que está na tela. Falso se algum campo está errado (o aviso já apareceu).</summary>
    private bool Gravar(out bool mudouCaixa)
    {
        mudouCaixa = NumeroCaixa != Principal.Config.NumeroCaixa;
        if (string.IsNullOrWhiteSpace(NomeEvento))
        {
            AbaSelecionada = 0;
            Principal.MostrarAviso("Informe o nome do evento.", erro: true);
            return false;
        }

        if (mudouCaixa && Principal.Sessao is not null)
        {
            Principal.MostrarAviso("Feche o caixa atual antes de trocar o número do caixa.", erro: true);
            return false;
        }

        if (!GravarAbas()) return false;

        Sistema.Config.Salvar(Montar());
        Rodape = Sistema.Config.Atual.Rodape; // o campo mostra o que foi gravado (sem repetir a mensagem fixa)
        CarregarAbas();
        MarcarComoSalva();
        return true;
    }

    /// <summary>
    /// Grava as abas como estão na lista: cria as novas (as sem nome ficam de fora), exclui as tiradas, troca os
    /// nomes e a ordem.
    /// </summary>
    private bool GravarAbas()
    {
        if (!AbasMudaram) return true;
        if (Abas.FirstOrDefault(a => !a.Nova && a.NomeLimpo.Length == 0) is not null)
        {
            AbaSelecionada = AbaBotoes;
            Principal.MostrarAviso("Escreva o nome de todas as abas.", erro: true);
            return false;
        }
        try
        {
            // Primeiro cria as novas: assim dá para trocar a única aba por outra
            var ids = new List<long>();
            foreach (var aba in Abas.Where(a => !a.Nova || a.NomeLimpo.Length > 0).ToList())
            {
                if (aba.Nova)
                    ids.Add(Sistema.Catalogo.SalvarAba(new Aba { Nome = aba.Nome, Colunas = aba.Colunas, Linhas = aba.Linhas }).Id);
                else
                    ids.Add(aba.Aba!.Id);
            }
            foreach (var excluida in _abasExcluidas) Sistema.Catalogo.ExcluirAba(excluida.Aba!.Id);
            foreach (var aba in Abas.Where(a => !a.Nova && a.Mudou))
            {
                aba.Aba!.Nome = aba.Nome;
                aba.Aba.Colunas = aba.Colunas;
                aba.Aba.Linhas = aba.Linhas;
                Sistema.Catalogo.SalvarAba(aba.Aba);
            }
            Sistema.Catalogo.ReordenarAbas(ids);
            return true;
        }
        catch (ErroDeNegocio e)
        {
            AbaSelecionada = AbaBotoes;
            Principal.MostrarAviso(e.Message, erro: true);
            CarregarAbas();
            return false;
        }
    }

    private const int AbaBotoes = 2;

    /// <summary>
    /// O backup e o "Apagar as vendas" usam o que está salvo. Se o evento, a ficha, o logotipo ou os botões foram
    /// mudados nesta tela e ainda não salvos, pergunta se salva antes. Falso se o operador voltou ou algo está errado.
    /// </summary>
    private async Task<bool> SalvarAlteracoesAntes(string antes)
    {
        // Compara tudo o que vai no backup (o que é da máquina — impressora, tela, pastas — fica de fora).
        var mudou = _salva is not null &&
                    (Json(Montar().ComDadosDaMaquina(_salva)) != Json(_salva) || AbasMudaram);
        if (!mudou) return true;
        if (!await Principal.Confirmar("Alterações não salvas",
                $"Você mudou o evento, a ficha, o logotipo ou os botões e ainda não tocou em Salvar. Salvar agora, {antes}?",
                "Salvar e continuar", "Voltar"))
            return false;
        return Gravar(out _);
    }

    [RelayCommand]
    private async Task ImprimirTeste()
    {
        ImprimindoTeste = true;
        try
        {
            var config = Montar();
            var resultado = await Task.Run(() => Sistema.Impressao.Teste(config));
            if (resultado.Ok)
                Principal.MostrarAviso(config.Impressora == Core.TipoImpressora.Arquivo
                    ? $"Teste salvo em {Sistema.Impressao.CriarDestino(config).Descricao}"
                    : "Teste enviado para a impressora.");
            else
                await Principal.Mensagem("A impressão falhou", resultado.Mensagem);
        }
        finally
        {
            ImprimindoTeste = false;
        }
    }

    /// <summary>
    /// Lista de novo as impressoras do Windows e as portas COM. Acontece sozinho ao entrar na aba Impressora
    /// (ex.: depois de instalar o driver da Elgin com o programa aberto).
    /// </summary>
    private void ProcurarImpressoras(string? impressora, string? porta)
    {
        Impressoras.Clear();
        foreach (var i in TransporteWindows.Impressoras()) Impressoras.Add(i);
        if (!string.IsNullOrEmpty(impressora) && !Impressoras.Contains(impressora)) Impressoras.Insert(0, impressora);
        Portas.Clear();
        foreach (var p in TransporteSerial.Portas()) Portas.Add(p);
        if (!string.IsNullOrEmpty(porta) && !Portas.Contains(porta)) Portas.Insert(0, porta);
        NomeImpressora = string.IsNullOrEmpty(impressora)
            ? Impressoras.FirstOrDefault(i => i.Contains("elgin", StringComparison.OrdinalIgnoreCase) ||
                                              i.Contains("i9", StringComparison.OrdinalIgnoreCase))
            : impressora;
        PortaSerial = porta;
        OnPropertyChanged(nameof(SemImpressoras));
    }

    partial void OnAbaSelecionadaChanged(int value)
    {
        if (value == AbaImpressora && !_carregando) ProcurarImpressoras(NomeImpressora, PortaSerial);
        // Saiu da aba Máquina: na próxima vez pede a senha técnica de novo (no modo teste, nunca pede).
        if (value != AbaMaquina) MaquinaLiberada = Principal.ModoTeste;
    }

    private const int AbaImpressora = 3;
    public const int AbaMaquina = 6;

    // Aba Máquina: só com a senha técnica da BC Fichas (ou no modo teste, que é a BC Fichas programando a máquina)
    [ObservableProperty] private bool _maquinaLiberada;
    [ObservableProperty] private string _senhaTecnicaDigitada = "";
    [ObservableProperty] private string _erroSenhaTecnica = "";
    [ObservableProperty] private bool _conferindoSenha;

    [RelayCommand]
    private async Task LiberarMaquina()
    {
        if (ConferindoSenha) return;
        var digitada = SenhaTecnicaDigitada;
        ConferindoSenha = true;
        var certa = await Task.Run(() => Principal.SenhaTecnica.Confere(digitada));
        ConferindoSenha = false;
        SenhaTecnicaDigitada = "";
        ErroSenhaTecnica = certa ? "" : "Senha técnica errada.";
        // Trocou de aba enquanto conferia: não deixa a aba Máquina aberta para a próxima vez
        MaquinaLiberada = certa && AbaSelecionada == AbaMaquina;
        if (certa) Principal.TecladoVisivel = false; // o campo da senha some; o teclado da tela também
    }

    [RelayCommand]
    private void MoverImpressao(string direcao)
    {
        var passo = direcao == "esquerda" ? -4 : direcao == "direita" ? 4 : -AjusteHorizontal;
        AjusteHorizontal = Math.Clamp(AjusteHorizontal + passo, -Configuracao.AjusteMaximo, Configuracao.AjusteMaximo);
    }

    /// <summary>Cria ou troca a senha. Ela é gravada na hora (não espera o "Salvar").</summary>
    [RelayCommand]
    private void DefinirSenha() => Principal.AbrirDialogo(new NovaSenhaViewModel(Principal, senha =>
    {
        SenhaMaster = senha;
        GravarSenha(senha);
        Principal.MostrarAviso("Senha master gravada.");
    }));

    [RelayCommand]
    private async Task RemoverSenha()
    {
        if (!await Principal.Confirmar("Tirar a senha master?",
                "Sem senha, qualquer pessoa abre todas as telas (configurações, relatórios, devolução...).",
                "Tirar a senha", "Voltar", perigo: true))
            return;
        SenhaMaster = "";
        GravarSenha("");
        Principal.MostrarAviso("Senha master removida.");
    }

    [RelayCommand]
    private void TravarTudo()
    {
        foreach (var p in Protecoes) p.Marcada = true;
    }

    [RelayCommand]
    private void LiberarTudo()
    {
        foreach (var p in Protecoes) p.Marcada = false;
    }

    // ---------- Aba Máquina: backup da programação e começar de novo ----------

    private void AtualizarSituacao()
    {
        var s = Sistema.Programacao.Situacao();
        var texto = $"Caixa {Principal.Config.NumeroCaixa:00} • {s.Produtos} produto(s) em {s.Abas} aba(s)";
        if (s.Combos > 0) texto += $" • {s.Combos} combo(s)";
        SituacaoMaquina = texto;
        MaquinaPura = s.Pura;
        if (s.Pura)
        {
            SituacaoVendas = "Pura: nenhuma venda, teste ou caixa guardado";
            return;
        }
        var vendas = "Guardado: " + DescreverVendas(s);
        if (s.CaixaAberto) vendas += " • caixa aberto";
        if (s.ModoTeste) vendas += " • modo teste ligado";
        SituacaoVendas = vendas;
    }

    /// <summary>"12 pedido(s) + 5 de teste em 3 caixa(s)".</summary>
    private static string DescreverVendas(SituacaoMaquina s)
    {
        var pedidos = s.Pedidos > 0 && s.PedidosTeste > 0 ? $"{s.Pedidos} pedido(s) + {s.PedidosTeste} de teste"
            : s.Pedidos > 0 ? $"{s.Pedidos} pedido(s)"
            : s.PedidosTeste > 0 ? $"{s.PedidosTeste} pedido(s) de teste"
            : "";
        return pedidos.Length > 0 ? $"{pedidos} em {s.Caixas} caixa(s)" : $"{s.Caixas} caixa(s) sem vendas";
    }

    /// <summary>O que mais acontece ao apagar as vendas (caixa aberto, modo teste, estoque), uma coisa por linha.</summary>
    private static string Avisos(SituacaoMaquina s, bool estoque)
    {
        var linhas = new List<string>();
        if (s.CaixaAberto)
            linhas.Add(s.PedidosNoCaixaAberto > 0
                ? $"Atenção: o caixa está aberto com {s.PedidosNoCaixaAberto} venda(s) e será apagado sem imprimir o fechamento."
                : "O caixa aberto também é apagado.");
        if (s.ModoTeste) linhas.Add("O modo teste é desligado.");
        if (estoque && s.EstoqueAVoltar > 0)
            linhas.Add($"O que as vendas tiraram do estoque ({s.EstoqueAVoltar} unidade(s)) volta para ele.");
        return linhas.Count == 0 ? "" : "\n\n" + string.Join("\n", linhas);
    }

    /// <summary>
    /// A pasta do campo, já gravada na configuração (vale na hora, sem tocar em Salvar). Devolve o caminho completo.
    /// </summary>
    private string GuardarPastaBackup()
    {
        var texto = string.IsNullOrWhiteSpace(PastaBackup) ? new Configuracao().PastaBackup : PastaBackup.Trim();
        if (texto != PastaBackup) PastaBackup = texto;
        if (texto != Principal.Config.PastaBackup)
        {
            var c = Principal.Config.Clonar();
            c.PastaBackup = texto;
            Sistema.Config.Salvar(c);
        }
        if (_salva is not null) _salva.PastaBackup = texto;
        AtualizarAlteracoes();
        return Core.Servicos.ProgramacaoServico.CaminhoDaPasta(texto);
    }

    public string NumeroCaixaTexto => $"{NumeroCaixa:00}";
    public string CaixaNaFicha => $"Sai na ficha e nos relatórios como CAIXA {NumeroCaixa:00}.";

    private bool TemCaixaAnterior => NumeroCaixa > 1;
    private bool TemProximoCaixa => NumeroCaixa < MaximoCaixas;

    [RelayCommand(CanExecute = nameof(TemCaixaAnterior))]
    private void CaixaAnterior() => NumeroCaixa--;

    [RelayCommand(CanExecute = nameof(TemProximoCaixa))]
    private void ProximoCaixa() => NumeroCaixa++;

    /// <summary>Tocar no número: digita direto no teclado numérico (mais rápido que o + quando é o caixa 15).</summary>
    [RelayCommand]
    private async Task DigitarCaixa()
    {
        var numero = new NumeroViewModel(Principal, "Número deste caixa (PDV)",
            "Cada máquina do evento tem um número diferente (sai na ficha e nos relatórios).", NumeroCaixa, MaximoCaixas);
        Principal.AbrirDialogo(numero);
        if (await numero.Resposta is { } caixa) NumeroCaixa = caixa;
    }

    [RelayCommand]
    private void EscolherMaquininha(TipoMaquininha tipo) =>
        TipoMaquininha = TiposMaquininha.FirstOrDefault(t => t.Valor == tipo) ?? TipoMaquininha;

    [RelayCommand]
    private async Task EscolherPastaFotos()
    {
        if (Principal.EscolherPasta is null) return;
        var atual = string.IsNullOrWhiteSpace(PastaFotos) ? new Configuracao().PastaFotos : PastaFotos.Trim();
        var pasta = await Principal.EscolherPasta(Directory.Exists(atual) ? atual : null);
        if (pasta is not null) PastaFotos = pasta;
    }

    [RelayCommand]
    private async Task EscolherPastaBackup()
    {
        if (Principal.EscolherPasta is null) return;
        var atual = Core.Servicos.ProgramacaoServico.CaminhoDaPasta(
            string.IsNullOrWhiteSpace(PastaBackup) ? new Configuracao().PastaBackup : PastaBackup);
        var pasta = await Principal.EscolherPasta(Directory.Exists(atual) ? atual : null);
        if (pasta is null) return;
        PastaBackup = pasta;
        GuardarPastaBackup();
        Principal.MostrarAviso("Pasta do backup gravada.");
    }

    /// <summary>
    /// Grava o backup da programação na pasta do backup e nos pendrives ligados. O backup nunca leva vendas; se
    /// esta máquina tem vendas ou testes guardados, oferece apagar para ela também ir pura para o cliente.
    /// </summary>
    [RelayCommand]
    private async Task FazerBackup()
    {
        if (!await SalvarAlteracoesAntes("antes do backup")) return;
        var pasta = GuardarPastaBackup();
        var nome = Sistema.Programacao.NomeArquivo();
        var arquivo = Path.Combine(pasta, nome);
        var pendrives = Principal.Pendrives();
        var copias = new List<string>();
        var falhas = new List<string>();

        Ocupado = true;
        try
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(pasta);
                Sistema.Programacao.Salvar(arquivo);
                foreach (var pendrive in pendrives)
                {
                    var copia = Path.Combine(pendrive, nome);
                    if (string.Equals(Path.GetFullPath(copia), Path.GetFullPath(arquivo), StringComparison.OrdinalIgnoreCase))
                        continue; // a pasta do backup já é o pendrive
                    try
                    {
                        File.Copy(arquivo, copia, overwrite: true);
                        copias.Add(pendrive);
                    }
                    catch (Exception e)
                    {
                        Log.Erro("Copiar o backup para o pendrive", e);
                        falhas.Add(pendrive);
                    }
                }
            });
        }
        catch (Exception e)
        {
            Log.Erro("Fazer backup", e);
            await Principal.Mensagem("Não consegui fazer o backup",
                (e is ErroDeNegocio ? e.Message : $"Erro ao gravar em {pasta}: {e.Message}") +
                "\n\nConfira a pasta do backup (ou escolha outra).");
            return;
        }
        finally
        {
            Ocupado = false;
        }

        var texto = $"Salvo em:\n{arquivo}";
        foreach (var copia in copias) texto += $"\ne no pendrive {copia}";
        foreach (var falha in falhas) texto += $"\nNão consegui copiar para o pendrive {falha}.";
        texto += "\n\nVai só a programação (produtos, combos, fotos, logotipo, ficha e senha), nunca as vendas. " +
                 "Na outra máquina: ponha o arquivo na pasta do backup dela (ou o pendrive) e toque em Restaurar.";

        var s = Sistema.Programacao.Situacao();
        if (s.Pura)
        {
            await Principal.Mensagem("Backup feito", texto);
            return;
        }
        if (s.CaixaAberto && s.PedidosNoCaixaAberto > 0)
        {
            // Pode ser um evento rolando: não oferece apagar o caixa com um toque.
            await Principal.Mensagem("Backup feito", texto + $"\n\nEsta máquina tem o caixa aberto com " +
                $"{s.PedidosNoCaixaAberto} venda(s). Para ela ir pura para o cliente, use Apagar as vendas nesta tela.");
            AtualizarSituacao();
            return;
        }
        texto = "Backup feito. " + texto + $"\n\nEsta máquina ainda tem {DescreverVendas(s)}. Apagar agora, para ela " +
                "também ir pura para o cliente?" + Avisos(s, estoque: true);
        if (await Principal.Confirmar("Apagar as vendas desta máquina?", texto, "Apagar as vendas", "Agora não", perigo: true))
            await ApagarVendasAgora(devolverEstoque: true);
        else
            AtualizarSituacao();
    }

    /// <summary>
    /// Restaura um backup (como copiar o banco de outra máquina): procura na pasta do backup e nos pendrives,
    /// mostra o que vem nele e pergunta só o número do caixa (PDV).
    /// </summary>
    [RelayCommand]
    private async Task Restaurar()
    {
        var pasta = GuardarPastaBackup();
        var lugares = new List<(string, string)> { (pasta, "Pasta do backup") };
        lugares.AddRange(Principal.Pendrives().Select(p => (p, "Pendrive " + p.TrimEnd('\\', '/'))));

        ResultadoProcura procura;
        Ocupado = true;
        try
        {
            procura = await Task.Run(() => Sistema.Programacao.Procurar(lugares));
        }
        finally
        {
            Ocupado = false;
        }

        var achados = procura.Achados;
        var podeProcurar = Principal.EscolherProgramacao is not null;
        string? arquivo;
        bool procurar;
        if (achados.Count == 1 && !podeProcurar)
        {
            arquivo = achados[0].Arquivo;
            procurar = false;
        }
        else if (achados.Count > 0)
        {
            // Mesmo com um só, mostra a lista: dá para ver de onde ele veio ou procurar outro arquivo.
            var lista = new EscolherBackupViewModel(Principal, achados, procura.Recusados, podeProcurar);
            Principal.AbrirDialogo(lista);
            var escolha = await lista.Resposta;
            arquivo = escolha.Arquivo;
            procurar = escolha.Procurar;
        }
        else
        {
            arquivo = null;
            string titulo, texto;
            if (procura.Recusados.Count > 0)
            {
                titulo = "Não dá para usar o backup";
                texto = EscolherBackupViewModel.DescreverRecusados(procura.Recusados, "\n\n", ":\n");
            }
            else
            {
                titulo = "Nenhum backup encontrado";
                texto = $"Não achei backup (arquivo .bcf) na pasta do backup nem em pendrive.\n\nPasta do backup: {pasta}\n\n" +
                        "Copie o arquivo para essa pasta (pelo acesso remoto) ou ponha o pendrive e toque em Restaurar de novo.";
            }
            if (!podeProcurar)
            {
                await Principal.Mensagem(titulo, texto);
                return;
            }
            procurar = await Principal.Confirmar(titulo, texto, "Procurar em outro lugar", "Voltar");
        }
        if (procurar && Principal.EscolherProgramacao is not null)
            arquivo = await Principal.EscolherProgramacao(Directory.Exists(pasta) ? pasta : null);
        if (arquivo is null) return;

        await RestaurarArquivo(arquivo);
    }

    private async Task RestaurarArquivo(string arquivo)
    {
        try
        {
            var resumo = Sistema.Programacao.Resumo(arquivo);
            var s = Sistema.Programacao.Situacao();
            var texto = $"Evento: {resumo.Evento}\n{resumo.Produtos} produto(s) em {resumo.Abas} aba(s)" +
                        (resumo.Combos > 0 ? $", {resumo.Combos} combo(s)" : "") +
                        $"\nSalvo em {Formato.DataHora(resumo.SalvoEm)} no Caixa {resumo.Caixa:00}.\nArquivo: {arquivo}\n\n" +
                        "Os produtos e as configurações desta máquina serão trocados por estes (a impressora e a tela " +
                        "continuam as desta máquina).";
            if (!s.Pura) texto += $" Também apaga o que está guardado aqui: {DescreverVendas(s)}.";
            texto += " Fica uma cópia de segurança." + Avisos(s, estoque: false);
            if (!await Principal.Confirmar("Restaurar este backup?", texto, "Restaurar", "Voltar")) return;

            var numero = new NumeroViewModel(Principal, "Número deste caixa (PDV)",
                "Cada máquina do evento tem um número diferente (sai na ficha e nos relatórios).", Principal.Config.NumeroCaixa,
                MaximoCaixas);
            Principal.AbrirDialogo(numero);
            if (await numero.Resposta is not { } caixa) return;

            Ocupado = true;
            await Task.Run(() => Sistema.Programacao.Carregar(arquivo, caixa));
            CacheImagens.Limpar();
            Principal.Venda.LimparPedido();
            Principal.MostrarAviso($"Backup restaurado: {resumo.Evento}, Caixa {caixa:00}.");
            Principal.Iniciar();
        }
        catch (ErroDeNegocio e)
        {
            await Principal.Mensagem("Não deu para restaurar", e.Message);
        }
        catch (Exception e)
        {
            Log.Erro("Restaurar backup", e);
            await Principal.Mensagem("Não deu para restaurar", "Erro ao ler o arquivo: " + e.Message);
        }
        finally
        {
            Ocupado = false;
        }
    }

    /// <summary>
    /// Deixa a máquina pura para o cliente (ou pronta para outra festa com os mesmos produtos): apaga vendas,
    /// testes e caixas, até o que estiver aberto. Produtos e configurações ficam.
    /// </summary>
    [RelayCommand]
    private async Task LimparVendas()
    {
        if (!await SalvarAlteracoesAntes("antes de apagar as vendas")) return;
        var s = Sistema.Programacao.Situacao();
        if (s.Pura)
        {
            await Principal.Mensagem("A máquina já está pura",
                "Não há vendas, testes nem caixas guardados. Os produtos e as configurações estão prontos para o cliente.");
            return;
        }
        if (!await Principal.Confirmar("Apagar as vendas?",
                $"Apaga desta máquina {DescreverVendas(s)}, com as sangrias e devoluções. Ficam os produtos, combos e " +
                "configurações, e o pedido volta para o 1." + Avisos(s, estoque: false) +
                "\n\nAntes, imprima ou anote os relatórios que precisar. Fica uma cópia de segurança.",
                "Apagar as vendas", "Voltar", perigo: true))
            return;
        // Para o cliente o estoque volta ao programado; numa festa nova com o que sobrou, fica como está.
        var devolver = s.EstoqueAVoltar > 0 && await Principal.Confirmar("E o estoque?",
            $"As vendas tiraram {s.EstoqueAVoltar} unidade(s) do estoque.\n\nPara entregar a máquina pura, o estoque " +
            "volta ao que era antes das vendas. Para outra festa com o que sobrou, deixe como está.",
            "Devolver ao estoque", "Deixar como está");
        await ApagarVendasAgora(devolver);
    }

    private Task ApagarVendasAgora(bool devolverEstoque) =>
        Executar(() => Sistema.Programacao.ApagarVendas(devolverEstoque),
            "Vendas apagadas: a máquina está pura, só com a programação.");

    /// <summary>
    /// Reprogramação para um novo evento (o banco vazio do sistema antigo): zera a programação e mantém só o que é
    /// da máquina.
    /// </summary>
    [RelayCommand]
    private async Task ZerarProgramacao()
    {
        var s = Sistema.Programacao.Situacao();
        if (!await Principal.Confirmar("Reprogramar para um novo evento?",
                "Zera a programação: apaga vendas, testes, produtos, abas, combos, evento e modelo da ficha. Ficam só o " +
                "número do caixa, a impressora, as opções de tela, as pastas e a senha master." + Avisos(s, estoque: false) +
                "\n\nFica uma cópia de segurança.",
                "Zerar programação", "Voltar", perigo: true))
            return;
        await Executar(() => Sistema.Programacao.ZerarProgramacao(),
            "Programação zerada. Cadastre os produtos do novo evento.");
    }

    private async Task Executar(Action acao, string aviso)
    {
        Ocupado = true;
        try
        {
            await Task.Run(acao);
            CacheImagens.Limpar();
            Principal.Venda.LimparPedido();
            Principal.MostrarAviso(aviso);
            Principal.Iniciar();
        }
        catch (ErroDeNegocio e)
        {
            await Principal.Mensagem("Não deu para apagar", e.Message);
        }
        catch (Exception e)
        {
            Log.Erro("Apagar", e);
            await Principal.Mensagem("Não deu para apagar", "Erro: " + e.Message);
        }
        finally
        {
            Ocupado = false;
        }
    }

    private void GravarSenha(string senha)
    {
        var c = Principal.Config.Clonar();
        c.SenhaMaster = senha;
        Sistema.Config.Salvar(c);
        if (_salva is not null) _salva.SenhaMaster = senha;
        AtualizarAlteracoes();
    }

    [RelayCommand]
    private async Task EscolherLogo()
    {
        if (Principal.EscolherImagem is null) return;
        var arquivo = await Principal.EscolherImagem(Path.GetDirectoryName(Principal.Config.PastaFotos), false);
        if (arquivo is null) return;
        try
        {
            Logo = Path.Combine("imagens", ImagemUtil.Importar(arquivo, Sistema.PastaImagens, "logo-", 600));
        }
        catch (Exception e)
        {
            Principal.MostrarAviso(e is ErroDeNegocio ? e.Message : "Não consegui abrir a imagem.", erro: true);
        }
    }

    [RelayCommand]
    private void RemoverLogo() => Logo = null;

    /// <summary>Linha nova e vazia para escrever o nome (a tela põe o cursor nela). Gravada ao tocar em Salvar.</summary>
    [RelayCommand]
    private void NovaAba() => Abas.Add(Linha(null));

    [RelayCommand]
    private async Task ExcluirAba(AbaEdicao aba)
    {
        if (!aba.Nova)
        {
            if (Sistema.Catalogo.Produtos().Any(p => p.AbaId == aba.Aba!.Id))
            {
                Principal.MostrarAviso("Esta aba ainda tem produtos. Mova ou exclua os produtos antes.", erro: true);
                return;
            }
            if (Abas.Count(a => a != aba && (!a.Nova || a.NomeLimpo.Length > 0)) == 0)
            {
                Principal.MostrarAviso("É preciso ter pelo menos uma aba.", erro: true);
                return;
            }
            if (!await Principal.Confirmar("Excluir aba", $"Excluir a aba {aba.Aba!.Nome}? Ela sai ao tocar em Salvar.",
                    "Excluir", "Voltar", perigo: true))
                return;
            _abasExcluidas.Add(aba);
        }
        Abas.Remove(aba);
    }

    [RelayCommand]
    private void SubirAba(AbaEdicao aba) => MoverAba(aba, -1);

    [RelayCommand]
    private void DescerAba(AbaEdicao aba) => MoverAba(aba, +1);

    private void MoverAba(AbaEdicao aba, int direcao)
    {
        var i = Abas.IndexOf(aba);
        var j = i + direcao;
        if (i < 0 || j < 0 || j >= Abas.Count) return;
        Abas.Move(i, j);
    }

    private void CarregarAbas()
    {
        Abas.Clear();
        var porAba = Sistema.Catalogo.Produtos().Where(p => p.Ativo).GroupBy(p => p.AbaId)
            .ToDictionary(g => g.Key, g => g.Count());
        foreach (var a in Sistema.Catalogo.Abas()) Abas.Add(Linha(a, porAba.GetValueOrDefault(a.Id)));
    }

    /// <summary>Como os botões desta aba se arrumam na tela de venda (com a prévia dos produtos dela).</summary>
    [RelayCommand]
    private void GradeDaAba(AbaEdicao aba)
    {
        var produtos = aba.Nova ? [] : Sistema.Catalogo.ProdutosDaAba(aba.Aba!.Id).OrderBy(p => p.Posicao).ToList();
        Principal.AbrirDialogo(new GradeAbaViewModel(Principal, aba, produtos));
    }

    private AbaEdicao Linha(Aba? aba, int produtos = 0)
    {
        var linha = new AbaEdicao(aba, produtos);
        linha.PropertyChanged += (_, _) => AtualizarAlteracoes();
        return linha;
    }

    private Configuracao Montar()
    {
        var c = Principal.Config.Clonar();
        c.NomeEvento = NomeEvento.Trim().ToUpperInvariant();
        c.Rodape = Configuracao.RodapeSemMensagemFixa(Rodape);
        c.NumeroCaixa = NumeroCaixa;
        c.DesligarAoFechar = DesligarAoFechar;
        c.ManterTelaLigada = ManterTelaLigada;
        c.IniciarComWindows = IniciarComWindows;
        c.TelaCheia = TelaCheia;
        c.TecladoNaTela = TecladoNaTela;
        c.Zoom = Zoom;
        c.Modelo = Modelo?.Valor ?? ModeloFicha.Classico2;
        c.Moldura = Moldura;
        c.Fonte = Fonte ?? "Impact";
        c.CodigoDeBarras = CodigoDeBarras;
        c.MostrarValorNaFicha = MostrarValor;
        c.Logo = Logo;
        c.PastaFotos = string.IsNullOrWhiteSpace(PastaFotos) ? new Configuracao().PastaFotos : PastaFotos.Trim();
        c.PastaBackup = string.IsNullOrWhiteSpace(PastaBackup) ? new Configuracao().PastaBackup : PastaBackup.Trim();
        c.Impressora = TipoImpressora?.Valor ?? Core.TipoImpressora.Windows;
        c.NomeImpressora = NomeImpressora ?? "";
        c.PortaSerial = PortaSerial ?? "COM3";
        c.BaudRate = BaudRate;
        c.LarguraPapelMm = LarguraPapel;
        c.Corte = Corte?.Valor ?? TipoCorte.Parcial;
        c.AjusteHorizontal = AjusteHorizontal;
        c.Maquininha = TipoMaquininha?.Valor ?? Core.TipoMaquininha.Separada;
        c.SimuladorAprovarEmSegundos = SimuladorSegundos;
        c.SenhaMaster = SenhaMaster.Trim();
        c.LiberarDevolucao = LiberarDevolucao;
        c.LiberarReimpressao = LiberarReimpressao;
        c.TelasProtegidas = Protecoes.Where(p => p.Marcada).Aggregate(TelaProtegida.Nenhuma, (t, p) => t | p.Tela);
        return c;
    }

    private void AoMudar(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TemAlteracoes) or nameof(Previa) or nameof(Ocupado) or nameof(SituacaoMaquina)
            or nameof(SituacaoVendas) or nameof(MaquinaPura) or nameof(AbaSelecionada) or nameof(ImprimindoTeste)
            or nameof(MaquinaLiberada) or nameof(SenhaTecnicaDigitada) or nameof(ErroSenhaTecnica)
            or nameof(ConferindoSenha))
            return;
        AtualizarAlteracoes();
        switch (e.PropertyName)
        {
            case nameof(TipoImpressora):
                OnPropertyChanged(nameof(ImpressoraWindows));
                OnPropertyChanged(nameof(ImpressoraSerial));
                OnPropertyChanged(nameof(ImpressoraArquivo));
                break;
            case nameof(Logo):
                OnPropertyChanged(nameof(TemLogo));
                break;
        }

        if (_carregando) return;
        if (e.PropertyName is nameof(NomeEvento) or nameof(Rodape) or nameof(NumeroCaixa) or nameof(Modelo)
            or nameof(Fonte) or nameof(CodigoDeBarras) or nameof(MostrarValor) or nameof(Logo) or nameof(LarguraPapel)
            or nameof(Moldura) or nameof(AjusteHorizontal))
        {
            _timerPrevia.Stop();
            _timerPrevia.Start();
        }
    }

    /// <summary>
    /// Saiu das configurações: a prévia da ficha (uma imagem do tamanho da ficha, fora da área do coletor) é solta
    /// na hora. Antes ficava presa até o coletor de memória passar, uma a cada vez que a tela era aberta.
    /// </summary>
    public override void AoSair()
    {
        _timerPrevia.Stop();
        var previa = Previa;
        Previa = null;
        previa?.Dispose();
    }

    private void AtualizarPrevia()
    {
        try
        {
            // Direto dos pontos da imagem (sem gravar e ler um PNG a cada letra digitada) e soltando a prévia anterior.
            using var bitmap = Sistema.Impressao.Previa(Montar());
            var antiga = Previa;
            Previa = new Bitmap(Avalonia.Platform.PixelFormat.Rgba8888, Avalonia.Platform.AlphaFormat.Premul,
                bitmap.GetPixels(), new Avalonia.PixelSize(bitmap.Width, bitmap.Height), new Avalonia.Vector(96, 96),
                bitmap.RowBytes);
            antiga?.Dispose();
        }
        catch (Exception e)
        {
            Log.Erro("Prévia da ficha", e);
        }
    }
}
