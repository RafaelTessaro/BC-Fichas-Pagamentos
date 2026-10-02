using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BCFichas.Core;

namespace BCFichas.App.ViewModels;

public sealed class ItemMenu(string titulo, string descricao, string icone, string cor, bool protegido, Action acao)
{
    public string Titulo { get; } = titulo;
    public string Descricao { get; } = descricao;
    public Geometry Icone { get; } = Recursos.Icone(icone);
    public IBrush Cor { get; } = new SolidColorBrush(Color.Parse(cor));
    public bool Protegido { get; } = protegido;
    public Action Acao { get; } = acao;
}

/// <summary>Barra de ferramentas (botão ☰ da tela de venda).</summary>
public sealed partial class MenuViewModel : ViewModelBase
{
    private readonly PrincipalViewModel _p;

    public MenuViewModel(PrincipalViewModel principal)
    {
        _p = principal;
        var c = principal.Config;
        Itens =
        [
            new("Produtos", "Cadastrar e editar botões", "IconeCaixaProduto", "#2563EB", c.Protegida(TelaProtegida.Produtos),
                () => _p.AbrirProtegido(TelaProtegida.Produtos, () => new ProdutosViewModel(_p))),
            new("Reimprimir fichas", "Segunda via de um pedido", "IconeImpressora", "#7C3AED", c.Protegida(TelaProtegida.Reimpressao),
                () => _p.AbrirProtegido(TelaProtegida.Reimpressao, () => new ReimpressaoViewModel(_p))),
            new("Devolver fichas", "Cliente não usou a ficha", "IconeTroca", "#DB2777", c.Protegida(TelaProtegida.Devolucao),
                () => _p.AbrirProtegido(TelaProtegida.Devolucao, () => new DevolucaoViewModel(_p))),
            new("Sangria / Suprimento", "Tirar ou pôr dinheiro", "IconeDinheiro", "#16A34A", c.Protegida(TelaProtegida.Sangria),
                () => _p.AbrirProtegido(TelaProtegida.Sangria, () => new SangriaViewModel(_p))),
            new("Relatórios", "Vendas, caixas e sangrias", "IconeGrafico", "#D97706", c.Protegida(TelaProtegida.Relatorios),
                () => _p.AbrirProtegido(TelaProtegida.Relatorios, () => new RelatoriosViewModel(_p))),
            principal.ModoTeste
                ? new("Sair do modo teste", "Apaga as vendas de teste", "IconeAlerta", "#EA580C", false,
                    () => _ = _p.SairDoModoTeste())
                : new("Fechar caixa", "Conferir e encerrar o dia", "IconeCadeado", "#DC2626", c.Protegida(TelaProtegida.FecharCaixa),
                    () => _p.AbrirProtegido(TelaProtegida.FecharCaixa, () => new FechamentoViewModel(_p))),
            new("Configurações", "Evento, ficha, impressora", "IconeConfig", "#475569", c.Protegida(TelaProtegida.Configuracao),
                () => _p.AbrirProtegido(TelaProtegida.Configuracao, () => new ConfiguracaoViewModel(_p))),
            new("Sair do programa", "Fecha o BC Fichas", "IconeDesligar", "#334155", c.Protegida(TelaProtegida.SairDoPrograma),
                () => _p.ExecutarProtegido(TelaProtegida.SairDoPrograma, Sair)),
        ];
    }

    public List<ItemMenu> Itens { get; }

    [RelayCommand]
    private void Escolher(ItemMenu item)
    {
        _p.FecharDialogo(this);
        item.Acao();
    }

    [RelayCommand]
    private void Fechar() => _p.FecharDialogo(this);

    private async void Sair()
    {
        if (await _p.Confirmar("Sair do programa", "Fechar o BC Fichas? O caixa continua aberto.", "Sair", "Voltar", perigo: true))
            _p.Sair();
    }
}

/// <summary>Pede a senha master num teclado numérico.</summary>
public sealed partial class SenhaViewModel(PrincipalViewModel principal, Action aoAcertar) : ViewModelBase
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Pontos))]
    private string _digitado = "";

    [ObservableProperty] private string _erro = "";

    public string Titulo { get; init; } = "Senha master";
    public string Pontos => new('●', Digitado.Length);

    [RelayCommand]
    private void Tecla(string tecla)
    {
        Erro = "";
        if (tecla == "<")
        {
            if (Digitado.Length > 0) Digitado = Digitado[..^1];
        }
        else if (tecla == "C")
        {
            Digitado = "";
        }
        else if (Digitado.Length < 12 && tecla.All(char.IsAsciiDigit))
        {
            Digitado += tecla;
        }
    }

    [RelayCommand]
    private void Confirmar()
    {
        if (Digitado == principal.Config.SenhaMaster)
        {
            principal.FecharDialogo(this);
            aoAcertar();
            return;
        }
        Erro = "Senha errada";
        Digitado = "";
    }

    [RelayCommand]
    private void Cancelar() => principal.FecharDialogo(this);
}

public sealed partial class MensagemViewModel : ViewModelBase
{
    private readonly PrincipalViewModel _p;
    private readonly TaskCompletionSource<bool> _resposta = new();

    public MensagemViewModel(PrincipalViewModel principal, string titulo, string texto, string sim, string? nao, bool perigo)
    {
        _p = principal;
        Titulo = titulo;
        Texto = texto;
        TextoSim = sim;
        TextoNao = nao;
        Perigo = perigo;
    }

    public string Titulo { get; }
    public string Texto { get; }
    public string TextoSim { get; }
    public string? TextoNao { get; }
    public bool TemNao => TextoNao is not null;
    public bool Perigo { get; }
    public bool Normal => !Perigo;
    public Task<bool> Resposta => _resposta.Task;

    [RelayCommand]
    private void Sim()
    {
        _p.FecharDialogo(this);
        _resposta.TrySetResult(true);
    }

    [RelayCommand]
    private void Nao()
    {
        _p.FecharDialogo(this);
        _resposta.TrySetResult(false);
    }
}

/// <summary>Cria ou troca a senha master: digita duas vezes no teclado numérico.</summary>
public sealed partial class NovaSenhaViewModel(PrincipalViewModel principal, Action<string> aoDefinir) : ViewModelBase
{
    private string? _primeira;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Pontos))]
    private string _digitado = "";

    [ObservableProperty] private string _erro = "";
    [ObservableProperty] private string _instrucao = "Digite a nova senha (4 a 12 números)";
    [ObservableProperty] private string _textoBotao = "Continuar";

    public string Pontos => Digitado.Length == 0 ? " " : new string('●', Digitado.Length);

    [RelayCommand]
    private void Tecla(string tecla)
    {
        Erro = "";
        if (tecla == "<")
        {
            if (Digitado.Length > 0) Digitado = Digitado[..^1];
        }
        else if (tecla == "C")
        {
            Digitado = "";
        }
        else if (Digitado.Length < 12 && tecla.All(char.IsAsciiDigit))
        {
            Digitado += tecla;
        }
    }

    [RelayCommand]
    private void Confirmar()
    {
        if (_primeira is null)
        {
            if (Digitado.Length < 4)
            {
                Erro = "Use pelo menos 4 números";
                return;
            }
            _primeira = Digitado;
            Digitado = "";
            Instrucao = "Digite a mesma senha de novo";
            TextoBotao = "Gravar senha";
            return;
        }

        if (Digitado != _primeira)
        {
            Erro = "As senhas não são iguais. Comece de novo.";
            _primeira = null;
            Digitado = "";
            Instrucao = "Digite a nova senha (4 a 12 números)";
            TextoBotao = "Continuar";
            return;
        }
        principal.FecharDialogo(this);
        aoDefinir(Digitado);
    }

    [RelayCommand]
    private void Cancelar() => principal.FecharDialogo(this);
}
