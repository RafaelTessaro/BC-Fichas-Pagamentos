using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BCFichas.Core;
using BCFichas.Core.Impressao;

namespace BCFichas.App.ViewModels;

public sealed record Opcao<T>(T Valor, string Texto)
{
    public override string ToString() => Texto;
}

public sealed partial class AbaEdicao(Aba aba) : ObservableObject
{
    public Aba Aba { get; } = aba;
    [ObservableProperty] private string _nome = aba.Nome;
}

public sealed partial class ProtecaoTela(TelaProtegida tela, string nome, bool marcada) : ObservableObject
{
    public TelaProtegida Tela { get; } = tela;
    public string Nome { get; } = nome;
    [ObservableProperty] private bool _marcada = marcada;
}

/// <summary>Todas as configurações do sistema, separadas em abas.</summary>
public sealed partial class ConfiguracaoViewModel : PaginaViewModel
{
    private readonly DispatcherTimer _timerPrevia;
    private bool _carregando;

    public ConfiguracaoViewModel(PrincipalViewModel principal) : base(principal)
    {
        _timerPrevia = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        {
            _timerPrevia!.Stop();
            AtualizarPrevia();
        });
        PropertyChanged += AoMudar;
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

    public List<int> Caixas { get; } = Enumerable.Range(1, 30).ToList();
    public List<int> OpcoesGrade { get; } = Enumerable.Range(2, 5).ToList();
    public List<int> Papeis { get; } = [80, 58];
    public List<int> Velocidades { get; } = [9600, 19200, 38400, 57600, 115200];
    public List<int> Zooms { get; } = [70, 80, 90, 100, 110, 125, 150];
    public List<int> SegundosSimulador { get; } = [0, 2, 3, 5, 10];
    public ObservableCollection<string> Fontes { get; } = new();
    public ObservableCollection<string> Impressoras { get; } = new();
    public ObservableCollection<string> Portas { get; } = new();
    public ObservableCollection<AbaEdicao> Abas { get; } = new();
    public List<ProtecaoTela> Protecoes { get; private set; } = new();

    [ObservableProperty] private int _abaSelecionada;

    // Geral
    [ObservableProperty] private string _nomeEvento = "";
    [ObservableProperty] private string _rodape = "";
    [ObservableProperty] private int _numeroCaixa = 1;
    [ObservableProperty] private bool _desligarAoFechar;
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
    [ObservableProperty] private int _colunas = 4;
    [ObservableProperty] private int _linhas = 3;

    // Impressora
    [ObservableProperty] private Opcao<TipoImpressora>? _tipoImpressora;
    [ObservableProperty] private string? _nomeImpressora;
    [ObservableProperty] private string? _portaSerial;
    [ObservableProperty] private int _baudRate = 115200;
    [ObservableProperty] private int _larguraPapel = 80;
    [ObservableProperty] private Opcao<TipoCorte>? _corte;
    [ObservableProperty] private bool _imprimindoTeste;

    // Maquininha
    [ObservableProperty] private int _simuladorSegundos;

    // Segurança
    [ObservableProperty] private string _senhaMaster = "";

    public bool TemLogo => Logo is not null;
    public bool ImpressoraWindows => TipoImpressora?.Valor == Core.TipoImpressora.Windows;
    public bool ImpressoraSerial => TipoImpressora?.Valor == Core.TipoImpressora.Serial;
    public bool ImpressoraArquivo => TipoImpressora?.Valor == Core.TipoImpressora.Arquivo;
    public string PastaArquivo => Sistema.Impressao.PastaPadraoArquivo;
    public string PastaDados => Sistema.PastaDados;
    public bool SemImpressoras => Impressoras.Count == 0;

    public override void AoAbrir()
    {
        _carregando = true;
        var c = Principal.Config;

        NomeEvento = c.NomeEvento;
        Rodape = c.Rodape;
        NumeroCaixa = c.NumeroCaixa;
        DesligarAoFechar = c.DesligarAoFechar;
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

        Colunas = c.Colunas;
        Linhas = c.Linhas;
        CarregarAbas();

        Impressoras.Clear();
        foreach (var i in TransporteWindows.Impressoras()) Impressoras.Add(i);
        if (!string.IsNullOrEmpty(c.NomeImpressora) && !Impressoras.Contains(c.NomeImpressora))
            Impressoras.Insert(0, c.NomeImpressora);
        Portas.Clear();
        foreach (var p in TransporteSerial.Portas()) Portas.Add(p);
        if (!Portas.Contains(c.PortaSerial)) Portas.Insert(0, c.PortaSerial);
        TipoImpressora = TiposImpressora.First(t => t.Valor == c.Impressora);
        NomeImpressora = string.IsNullOrEmpty(c.NomeImpressora)
            ? Impressoras.FirstOrDefault(i => i.Contains("elgin", StringComparison.OrdinalIgnoreCase) ||
                                              i.Contains("i9", StringComparison.OrdinalIgnoreCase))
            : c.NomeImpressora;
        PortaSerial = c.PortaSerial;
        BaudRate = c.BaudRate;
        LarguraPapel = c.LarguraPapelMm <= 58 ? 58 : 80;
        Corte = Cortes.First(x => x.Valor == c.Corte);

        SimuladorSegundos = SegundosSimulador.Contains(c.SimuladorAprovarEmSegundos) ? c.SimuladorAprovarEmSegundos : 0;

        SenhaMaster = c.SenhaMaster;
        Protecoes =
        [
            new(TelaProtegida.Configuracao, "Configurações", c.TelasProtegidas.HasFlag(TelaProtegida.Configuracao)),
            new(TelaProtegida.Produtos, "Produtos", c.TelasProtegidas.HasFlag(TelaProtegida.Produtos)),
            new(TelaProtegida.Reimpressao, "Reimprimir fichas", c.TelasProtegidas.HasFlag(TelaProtegida.Reimpressao)),
            new(TelaProtegida.Sangria, "Sangria / Suprimento", c.TelasProtegidas.HasFlag(TelaProtegida.Sangria)),
            new(TelaProtegida.Relatorios, "Relatórios", c.TelasProtegidas.HasFlag(TelaProtegida.Relatorios)),
            new(TelaProtegida.FecharCaixa, "Fechar caixa", c.TelasProtegidas.HasFlag(TelaProtegida.FecharCaixa)),
            new(TelaProtegida.SairDoPrograma, "Sair do programa", c.TelasProtegidas.HasFlag(TelaProtegida.SairDoPrograma)),
        ];
        OnPropertyChanged(nameof(Protecoes));
        OnPropertyChanged(nameof(SemImpressoras));

        _carregando = false;
        AtualizarPrevia();
    }

    [RelayCommand]
    private void Salvar()
    {
        if (string.IsNullOrWhiteSpace(NomeEvento))
        {
            AbaSelecionada = 0;
            Principal.MostrarAviso("Informe o nome do evento.", erro: true);
            return;
        }

        var mudouCaixa = NumeroCaixa != Principal.Config.NumeroCaixa;
        if (mudouCaixa && Principal.Sessao is not null)
        {
            Principal.MostrarAviso("Feche o caixa atual antes de trocar o número do caixa.", erro: true);
            return;
        }

        foreach (var aba in Abas)
        {
            if (aba.Nome.Trim().ToUpperInvariant() == aba.Aba.Nome) continue;
            try
            {
                aba.Aba.Nome = aba.Nome;
                Sistema.Catalogo.SalvarAba(aba.Aba);
            }
            catch (ErroDeNegocio e)
            {
                AbaSelecionada = 2;
                Principal.MostrarAviso(e.Message, erro: true);
                return;
            }
        }

        Sistema.Config.Salvar(Montar());
        Rodape = Sistema.Config.Atual.Rodape; // o campo mostra o que foi gravado (sem repetir a mensagem fixa)
        Principal.MostrarAviso("Configurações salvas.");
        CarregarAbas();
        if (mudouCaixa) Principal.Iniciar();
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

    [RelayCommand]
    private void AtualizarImpressoras()
    {
        var atual = NomeImpressora;
        Impressoras.Clear();
        foreach (var i in TransporteWindows.Impressoras()) Impressoras.Add(i);
        Portas.Clear();
        foreach (var p in TransporteSerial.Portas()) Portas.Add(p);
        NomeImpressora = Impressoras.Contains(atual ?? "") ? atual : Impressoras.FirstOrDefault();
        OnPropertyChanged(nameof(SemImpressoras));
    }

    [RelayCommand]
    private async Task EscolherLogo()
    {
        if (Principal.EscolherImagem is null) return;
        var arquivo = await Principal.EscolherImagem();
        if (arquivo is null) return;
        try
        {
            var relativo = Path.Combine("imagens", "logo-" + DateTime.Now.Ticks + ".png");
            ImagemUtil.Importar(arquivo, Path.Combine(Sistema.PastaDados, relativo), 600);
            Logo = relativo;
        }
        catch (Exception e)
        {
            Principal.MostrarAviso(e is ErroDeNegocio ? e.Message : "Não consegui abrir a imagem.", erro: true);
        }
    }

    [RelayCommand]
    private void RemoverLogo() => Logo = null;

    [RelayCommand]
    private void NovaAba()
    {
        try
        {
            Sistema.Catalogo.SalvarAba(new Aba { Nome = $"ABA {Abas.Count + 1}" });
            CarregarAbas();
        }
        catch (ErroDeNegocio e)
        {
            Principal.MostrarAviso(e.Message, erro: true);
        }
    }

    [RelayCommand]
    private async Task ExcluirAba(AbaEdicao aba)
    {
        if (!await Principal.Confirmar("Excluir aba", $"Excluir a aba {aba.Aba.Nome}?", "Excluir", "Voltar", perigo: true))
            return;
        try
        {
            Sistema.Catalogo.ExcluirAba(aba.Aba.Id);
            CarregarAbas();
        }
        catch (ErroDeNegocio e)
        {
            Principal.MostrarAviso(e.Message, erro: true);
        }
    }

    [RelayCommand]
    private void SubirAba(AbaEdicao aba) => MoverAba(aba, -1);

    [RelayCommand]
    private void DescerAba(AbaEdicao aba) => MoverAba(aba, +1);

    private void MoverAba(AbaEdicao aba, int direcao)
    {
        var ids = Abas.Select(a => a.Aba.Id).ToList();
        var i = ids.IndexOf(aba.Aba.Id);
        var j = i + direcao;
        if (i < 0 || j < 0 || j >= ids.Count) return;
        (ids[i], ids[j]) = (ids[j], ids[i]);
        Sistema.Catalogo.ReordenarAbas(ids);
        CarregarAbas();
    }

    private void CarregarAbas()
    {
        Abas.Clear();
        foreach (var a in Sistema.Catalogo.Abas()) Abas.Add(new AbaEdicao(a));
    }

    private Configuracao Montar()
    {
        var c = Principal.Config.Clonar();
        c.NomeEvento = NomeEvento.Trim().ToUpperInvariant();
        c.Rodape = Configuracao.RodapeSemMensagemFixa(Rodape);
        c.NumeroCaixa = NumeroCaixa;
        c.DesligarAoFechar = DesligarAoFechar;
        c.TelaCheia = TelaCheia;
        c.TecladoNaTela = TecladoNaTela;
        c.Zoom = Zoom;
        c.Modelo = Modelo?.Valor ?? ModeloFicha.Classico2;
        c.Moldura = Moldura;
        c.Fonte = Fonte ?? "Impact";
        c.CodigoDeBarras = CodigoDeBarras;
        c.MostrarValorNaFicha = MostrarValor;
        c.Logo = Logo;
        c.Colunas = Colunas;
        c.Linhas = Linhas;
        c.Impressora = TipoImpressora?.Valor ?? Core.TipoImpressora.Windows;
        c.NomeImpressora = NomeImpressora ?? "";
        c.PortaSerial = PortaSerial ?? "COM3";
        c.BaudRate = BaudRate;
        c.LarguraPapelMm = LarguraPapel;
        c.Corte = Corte?.Valor ?? TipoCorte.Parcial;
        c.SimuladorAprovarEmSegundos = SimuladorSegundos;
        c.SenhaMaster = SenhaMaster.Trim();
        c.TelasProtegidas = Protecoes.Where(p => p.Marcada).Aggregate(TelaProtegida.Nenhuma, (t, p) => t | p.Tela);
        return c;
    }

    private void AoMudar(object? sender, PropertyChangedEventArgs e)
    {
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
            or nameof(Moldura))
        {
            _timerPrevia.Stop();
            _timerPrevia.Start();
        }
    }

    private void AtualizarPrevia()
    {
        try
        {
            using var bitmap = Sistema.Impressao.Previa(Montar());
            using var png = new MemoryStream(ImagemUtil.Png(bitmap));
            Previa = new Bitmap(png);
        }
        catch (Exception e)
        {
            Log.Erro("Prévia da ficha", e);
        }
    }
}
