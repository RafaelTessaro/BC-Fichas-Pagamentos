using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BCFichas.Core;
using BCFichas.Core.Impressao;

namespace BCFichas.App.ViewModels;

public sealed record LinhaValor(string Nome, string Valor, string Detalhe = "", bool Destaque = false);

/// <summary>Resumo de um caixa pronto para a tela (relatórios e fechamento).</summary>
public sealed class ResumoVM
{
    public ResumoVM(ResumoCaixa r)
    {
        Resumo = r;
        Titulo = Formato.Caixa(r.Sessao.Caixa, r.Sessao.Operador);
        Periodo = $"Aberto em {Formato.DataHora(r.Sessao.AbertaEm)}" +
                  (r.Sessao.FechadaEm is { } f ? $" • fechado em {Formato.DataHora(f)}" : " • aberto agora");
        TotalVendido = Dinheiro.Formatar(r.TotalVendas);
        Pedidos = r.QuantidadePedidos.ToString(CultureInfo.InvariantCulture);
        Fichas = r.QuantidadeFichas.ToString(CultureInfo.InvariantCulture);
        TicketMedio = Dinheiro.Formatar(r.TicketMedio);
        Formas = Enum.GetValues<FormaPagamento>()
            .Select(f => new LinhaValor(Nomes.De(f), Dinheiro.Formatar(r.Total(f)),
                $"{r.PedidosPorForma.GetValueOrDefault(f)} pedido(s)"))
            .ToList();
        Gaveta =
        [
            new("Abertura (troco)", Dinheiro.Formatar(r.Sessao.ValorAberturaCentavos)),
            new("+ Vendas em dinheiro", Dinheiro.Formatar(r.Total(FormaPagamento.Dinheiro))),
            new("+ Suprimentos", Dinheiro.Formatar(r.Suprimentos)),
            new("− Sangrias", Dinheiro.Formatar(r.Sangrias)),
            new("= Dinheiro esperado", Dinheiro.Formatar(r.DinheiroEsperado), Destaque: true),
        ];
        if (r.Sessao.ValorContadoCentavos is { } contado)
        {
            var diferenca = contado - r.DinheiroEsperado;
            Gaveta.Add(new("Contado na gaveta", Dinheiro.Formatar(contado)));
            Gaveta.Add(new(diferenca == 0 ? "Diferença" : diferenca > 0 ? "Sobra" : "Falta",
                Dinheiro.Formatar(Math.Abs(diferenca)), Destaque: true));
        }
        Produtos = r.Produtos
            .Select(p => new LinhaValor(p.Nome, Dinheiro.Formatar(p.TotalCentavos), $"{p.Quantidade} un."))
            .ToList();
    }

    public ResumoCaixa Resumo { get; }
    public string Titulo { get; }
    public string Periodo { get; }
    public string TotalVendido { get; }
    public string Pedidos { get; }
    public string Fichas { get; }
    public string TicketMedio { get; }
    public List<LinhaValor> Formas { get; }
    public List<LinhaValor> Gaveta { get; }
    public List<LinhaValor> Produtos { get; }
    public bool SemProdutos => Produtos.Count == 0;
}

/// <summary>Abrir o caixa: operador e troco inicial.</summary>
public sealed partial class AberturaViewModel(PrincipalViewModel principal) : PaginaViewModel(principal)
{
    // Os caixas são identificados só pelo número (Configurações → Número deste caixa), sem nome de operador.
    public EntradaValor Troco { get; } = new();
    public string Caixa => $"Caixa {Principal.Config.NumeroCaixa:00}";
    public string NomeEvento => Principal.Config.NomeEvento;

    [RelayCommand]
    private void AbrirCaixa()
    {
        try
        {
            var sessao = Sistema.Caixa.Abrir(Principal.Config.NumeroCaixa, null, Troco.Centavos);
            Principal.CaixaAberto(sessao);
            Principal.MostrarAviso($"{Caixa} aberto. Boas vendas!");
        }
        catch (ErroDeNegocio e)
        {
            Principal.MostrarAviso(e.Message, erro: true);
        }
    }

    [RelayCommand]
    private void Configurar() =>
        Principal.AbrirProtegido(TelaProtegida.Configuracao, () => new ConfiguracaoViewModel(Principal));

    [RelayCommand]
    private void Sair() => Principal.ExecutarProtegido(TelaProtegida.SairDoPrograma, Principal.Sair);

    protected override void Voltar()
    {
        // Sem caixa aberto não há tela de venda para voltar.
    }
}

/// <summary>Fechar o caixa: confere o dinheiro, imprime o fechamento e encerra a sessão.</summary>
public sealed partial class FechamentoViewModel : PaginaViewModel
{
    public FechamentoViewModel(PrincipalViewModel principal) : base(principal)
    {
        Contado.PropertyChanged += (_, _) => AtualizarDiferenca();
    }

    [ObservableProperty] private ResumoVM? _resumo;
    [ObservableProperty] private bool _informarContado = true;
    [ObservableProperty] private string _diferenca = "";
    [ObservableProperty] private bool _diferencaNegativa;
    [ObservableProperty] private bool _ocupado;
    public EntradaValor Contado { get; } = new();
    public bool DesligaAoFechar => Principal.Config.DesligarAoFechar;

    public override void AoAbrir()
    {
        if (Principal.Sessao is null) return;
        Resumo = new ResumoVM(Sistema.Caixa.Resumo(Principal.Sessao.Id));
        AtualizarDiferenca();
    }

    partial void OnInformarContadoChanged(bool value) => AtualizarDiferenca();

    [RelayCommand]
    private Task ImprimirParcial()
    {
        if (Principal.Sessao is null) return Task.CompletedTask;
        var resumo = Sistema.Caixa.Resumo(Principal.Sessao.Id);
        return Principal.ImprimirAsync(() =>
            Sistema.Impressao.Documento(Relatorios.Fechamento(resumo, Principal.Config, parcial: true), "Parcial impressa"));
    }

    [RelayCommand]
    private async Task FecharCaixa()
    {
        var sessao = Principal.Sessao;
        if (sessao is null || Ocupado) return;

        var texto = "Depois de fechar não dá para vender neste caixa até abrir de novo.";
        if (Principal.Config.DesligarAoFechar) texto += "\n\nO computador vai desligar em seguida.";
        if (!await Principal.Confirmar("Fechar o caixa?", texto, "Fechar caixa", "Voltar", perigo: true)) return;

        Ocupado = true;
        try
        {
            var resumo = Sistema.Caixa.Fechar(sessao, InformarContado ? Contado.Centavos : null);
            await Principal.ImprimirAsync(() =>
                Sistema.Impressao.Documento(Relatorios.Fechamento(resumo, Principal.Config), "Fechamento impresso"));

            if (Principal.Config.DesligarAoFechar)
            {
                Principal.DesligarComputador();
                return;
            }
            Principal.CaixaFechado();
            Principal.MostrarAviso("Caixa fechado.");
        }
        catch (ErroDeNegocio e)
        {
            await Principal.Mensagem("Não foi possível fechar", e.Message);
        }
        finally
        {
            Ocupado = false;
        }
    }

    private void AtualizarDiferenca()
    {
        if (Resumo is null || !InformarContado)
        {
            Diferenca = "";
            return;
        }
        var d = Contado.Centavos - Resumo.Resumo.DinheiroEsperado;
        DiferencaNegativa = d < 0;
        Diferenca = d == 0 ? "Confere com o esperado" : d > 0 ? $"Sobra de {Dinheiro.Formatar(d)}" : $"Falta {Dinheiro.Formatar(-d)}";
    }
}

public sealed record MovimentoItem(string Hora, string Tipo, string Valor, string Motivo, bool Sangria);

/// <summary>Sangria (tirar dinheiro) e suprimento (pôr dinheiro).</summary>
public sealed partial class SangriaViewModel(PrincipalViewModel principal) : PaginaViewModel(principal)
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EhSuprimento), nameof(TextoBotao))]
    private bool _ehSangria = true;

    [ObservableProperty] private string _motivo = "";
    [ObservableProperty] private string _dinheiroEmCaixa = "";
    [ObservableProperty] private bool _imprimirComprovante = true;

    public EntradaValor Valor { get; } = new();
    public ObservableCollection<MovimentoItem> Movimentos { get; } = new();
    public bool EhSuprimento => !EhSangria;
    public string TextoBotao => EhSangria ? "Registrar sangria" : "Registrar suprimento";

    public static readonly string[] Notas = ["50", "100", "200", "500", "1000", "2000", "5000", "10000"];

    public override void AoAbrir() => Atualizar();

    [RelayCommand]
    private void Tipo(string tipo) => EhSangria = tipo == "sangria";

    [RelayCommand]
    private async Task Salvar()
    {
        var sessao = Principal.Sessao;
        if (sessao is null) return;
        try
        {
            var movimento = Sistema.Caixa.RegistrarMovimento(sessao,
                EhSangria ? TipoMovimento.Sangria : TipoMovimento.Suprimento, Valor.Centavos, Motivo);
            Principal.MostrarAviso($"{(EhSangria ? "Sangria" : "Suprimento")} de {Dinheiro.Formatar(movimento.ValorCentavos)} registrado.");
            Valor.Centavos = 0;
            Motivo = "";
            Atualizar();
            if (ImprimirComprovante)
            {
                var noCaixa = Sistema.Caixa.Resumo(sessao.Id).DinheiroEsperado;
                await Principal.ImprimirAsync(() =>
                    Sistema.Impressao.Documento(Relatorios.Movimento(movimento, Principal.Config, noCaixa), "Comprovante impresso"));
            }
        }
        catch (ErroDeNegocio e)
        {
            Principal.MostrarAviso(e.Message, erro: true);
        }
    }

    private void Atualizar()
    {
        var sessao = Principal.Sessao;
        Movimentos.Clear();
        if (sessao is null) return;
        DinheiroEmCaixa = Dinheiro.Formatar(Sistema.Caixa.Resumo(sessao.Id).DinheiroEsperado);
        foreach (var m in Sistema.Caixa.Movimentos(sessao.Id))
        {
            Movimentos.Add(new MovimentoItem(m.CriadoEm.ToString("HH:mm", CultureInfo.InvariantCulture),
                m.Tipo == TipoMovimento.Sangria ? "Sangria" : "Suprimento",
                (m.Tipo == TipoMovimento.Sangria ? "− " : "+ ") + Dinheiro.Formatar(m.ValorCentavos),
                m.Motivo, m.Tipo == TipoMovimento.Sangria));
        }
    }
}
