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
                () => AbrirPeloMenu(TelaProtegida.Produtos, () => new ProdutosViewModel(_p))),
            new("Reimprimir fichas", "Segunda via de um pedido", "IconeImpressora", "#7C3AED", c.Protegida(TelaProtegida.Reimpressao),
                () => AbrirPeloMenu(TelaProtegida.Reimpressao, () => new ReimpressaoViewModel(_p))),
            new("Devolver fichas", "Cliente não usou a ficha", "IconeTroca", "#DB2777", c.Protegida(TelaProtegida.Devolucao),
                () => AbrirPeloMenu(TelaProtegida.Devolucao, () => new DevolucaoViewModel(_p))),
            new("Sangria / Suprimento", "Tirar ou pôr dinheiro", "IconeDinheiro", "#16A34A", c.Protegida(TelaProtegida.Sangria),
                () => AbrirPeloMenu(TelaProtegida.Sangria, () => new SangriaViewModel(_p))),
            new("Relatórios", "Vendas, caixas e sangrias", "IconeGrafico", "#D97706", c.Protegida(TelaProtegida.Relatorios),
                () => AbrirPeloMenu(TelaProtegida.Relatorios, () => new RelatoriosViewModel(_p))),
            principal.ModoTeste
                ? new("Sair do modo teste", "Apaga as vendas de teste", "IconeAlerta", "#EA580C", false,
                    () => _ = _p.SairDoModoTeste())
                : new("Fechar caixa", "Conferir e encerrar o dia", "IconeCadeado", "#DC2626", c.Protegida(TelaProtegida.FecharCaixa),
                    () => AbrirPeloMenu(TelaProtegida.FecharCaixa, () => new FechamentoViewModel(_p))),
            new("Configurações", "Evento, ficha, impressora", "IconeConfig", "#475569", c.Protegida(TelaProtegida.Configuracao),
                () => AbrirPeloMenu(TelaProtegida.Configuracao, () => new ConfiguracaoViewModel(_p))),
            new("Sair do programa", "Fecha o BC Fichas", "IconeDesligar", "#334155", c.Protegida(TelaProtegida.SairDoPrograma),
                () => _p.ExecutarProtegido(TelaProtegida.SairDoPrograma, Sair)),
        ];
    }

    public List<ItemMenu> Itens { get; }

    /// <summary>Tela aberta pelo menu: o Voltar dela traz o menu de volta.</summary>
    private void AbrirPeloMenu(TelaProtegida tela, Func<PaginaViewModel> criar) =>
        _p.AbrirProtegido(tela, () =>
        {
            var pagina = criar();
            pagina.VoltaParaMenu = true;
            return pagina;
        });

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

/// <summary>Pede um número no teclado numérico (ex.: número do caixa ao carregar uma programação).</summary>
public sealed partial class NumeroViewModel : ViewModelBase
{
    private readonly PrincipalViewModel _p;
    private readonly int _maximo;
    private readonly TaskCompletionSource<int?> _resposta = new();

    public NumeroViewModel(PrincipalViewModel principal, string titulo, string texto, int inicial, int maximo = 99)
    {
        _p = principal;
        _maximo = maximo;
        Titulo = titulo;
        Texto = texto;
        _digitado = inicial.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public string Titulo { get; }
    public string Texto { get; }
    public Task<int?> Resposta => _resposta.Task;

    [ObservableProperty] private string _digitado;
    [ObservableProperty] private string _erro = "";

    [RelayCommand]
    private void Tecla(string tecla)
    {
        Erro = "";
        if (tecla == "<") Digitado = Digitado.Length > 0 ? Digitado[..^1] : "";
        else if (tecla == "C") Digitado = "";
        else if (Digitado.Length < 2 && tecla.All(char.IsAsciiDigit)) Digitado = Digitado == "0" ? tecla : Digitado + tecla;
    }

    [RelayCommand]
    private void Confirmar()
    {
        if (!int.TryParse(Digitado, out var numero) || numero < 1 || numero > _maximo)
        {
            Erro = $"Digite um número de 1 a {_maximo}";
            return;
        }
        _p.FecharDialogo(this);
        _resposta.TrySetResult(numero);
    }

    [RelayCommand]
    private void Cancelar()
    {
        _p.FecharDialogo(this);
        _resposta.TrySetResult(null);
    }
}

/// <summary>O que foi escolhido na lista do "Restaurar": um arquivo, procurar em outro lugar ou nada (voltar).</summary>
public sealed record EscolhaBackup(string? Arquivo, bool Procurar = false);

/// <summary>Um backup na lista do "Restaurar".</summary>
public sealed class BackupItem(Core.Servicos.ProgramacaoEncontrada achado)
{
    public string Arquivo { get; } = achado.Arquivo;
    public string Evento { get; } = achado.Resumo.Evento.Length > 0 ? achado.Resumo.Evento : "(evento sem nome)";
    public string Lugar { get; } = achado.Lugar;
    public string Detalhes { get; } =
        $"Salvo em {Formato.DataHora(achado.Resumo.SalvoEm)} no Caixa {achado.Resumo.Caixa:00} • " +
        $"{achado.Resumo.Produtos} produto(s)" + (achado.Resumo.Combos > 0 ? $" • {achado.Resumo.Combos} combo(s)" : "");
    public string NomeArquivo { get; } = Path.GetFileName(achado.Arquivo);
}

/// <summary>
/// Lista dos backups achados na pasta do backup e nos pendrives (o mais novo primeiro), para escolher qual
/// restaurar ou procurar outro arquivo.
/// </summary>
public sealed partial class EscolherBackupViewModel : ViewModelBase
{
    private readonly PrincipalViewModel _p;
    private readonly TaskCompletionSource<EscolhaBackup> _resposta = new();

    public EscolherBackupViewModel(PrincipalViewModel principal, IEnumerable<Core.Servicos.ProgramacaoEncontrada> achados,
        IEnumerable<Core.Servicos.ArquivoRecusado> recusados, bool podeProcurar)
    {
        _p = principal;
        Itens = achados.Select(a => new BackupItem(a)).ToList();
        Recusados = DescreverRecusados(recusados.ToList(), "\n", ": ");
        PodeProcurar = podeProcurar;
        Explicacao = Itens.Count == 1
            ? "Achei este. Toque nele para ver o que tem e restaurar, ou procure outro arquivo."
            : "Achei estes na pasta do backup e nos pendrives. O mais novo está em cima.";
    }

    /// <summary>"arquivo (lugar): motivo" dos 3 primeiros e quantos mais (para a mensagem caber na tela).</summary>
    public static string DescreverRecusados(IReadOnlyList<Core.Servicos.ArquivoRecusado> recusados, string entre,
        string antesDoMotivo)
    {
        const int Mostrados = 3;
        var texto = string.Join(entre, recusados.Take(Mostrados)
            .Select(r => $"{Path.GetFileName(r.Arquivo)} ({r.Lugar}){antesDoMotivo}{r.Motivo}"));
        if (recusados.Count > Mostrados)
            texto += $"{entre}... e mais {recusados.Count - Mostrados} arquivo(s) que não dá para usar.";
        return texto;
    }

    public List<BackupItem> Itens { get; }
    public string Explicacao { get; }
    /// <summary>Arquivos .bcf achados que não dá para usar, com o motivo (vazio se não tem).</summary>
    public string Recusados { get; }
    public bool TemRecusados => Recusados.Length > 0;
    public bool PodeProcurar { get; }
    public Task<EscolhaBackup> Resposta => _resposta.Task;

    [RelayCommand]
    private void Escolher(BackupItem item) => Responder(new EscolhaBackup(item.Arquivo));

    [RelayCommand]
    private void Procurar() => Responder(new EscolhaBackup(null, Procurar: true));

    [RelayCommand]
    private void Cancelar() => Responder(new EscolhaBackup(null));

    private void Responder(EscolhaBackup escolha)
    {
        _p.FecharDialogo(this);
        _resposta.TrySetResult(escolha);
    }
}
