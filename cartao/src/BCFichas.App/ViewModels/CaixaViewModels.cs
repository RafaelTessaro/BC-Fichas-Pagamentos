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
        ];
        if (r.Devolvido(FormaPagamento.Dinheiro) > 0)
            Gaveta.Add(new("− Devoluções", Dinheiro.Formatar(r.Devolvido(FormaPagamento.Dinheiro))));
        Gaveta.Add(new("= Dinheiro esperado", Dinheiro.Formatar(r.DinheiroEsperado), Destaque: true));
        TemDevolucoes = r.TemDevolucoes;
        TotalDevolvido = "− " + Dinheiro.Formatar(r.TotalDevolvido);
        VendaLiquida = Dinheiro.Formatar(r.VendaLiquida);
        Devolucoes = Enum.GetValues<FormaPagamento>()
            .Where(f => r.Devolvido(f) > 0)
            .Select(f => new LinhaValor(Nomes.De(f), "− " + Dinheiro.Formatar(r.Devolvido(f)),
                f == FormaPagamento.Dinheiro ? "saiu da gaveta" : "estorno na maquininha"))
            .Concat(r.ProdutosDevolvidos.Select(p =>
                new LinhaValor(p.Nome, "− " + Dinheiro.Formatar(p.TotalCentavos), $"{p.Quantidade} ficha(s) devolvida(s)")))
            .ToList();
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
    public bool TemDevolucoes { get; }
    public string TotalDevolvido { get; } = "";
    public string VendaLiquida { get; } = "";
    public List<LinhaValor> Devolucoes { get; } = new();
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
            // Comprovante da abertura: troco inicial, data e hora (fica com o responsável pelo caixa).
            var config = Principal.Config;
            _ = Principal.ImprimirAsync(() => Sistema.Impressao.Documento(Relatorios.Abertura(sessao, config), "Abertura impressa"));
        }
        catch (ErroDeNegocio e)
        {
            Principal.MostrarAviso(e.Message, erro: true);
        }
    }

    /// <summary>Toque no logo da BC Fichas (5 toques seguidos ligam o modo teste).</summary>
    [RelayCommand]
    private void ToqueNaMarca() => Principal.ToqueNaMarca();

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

    /// <summary>Fechando e imprimindo o fechamento: o Voltar espera terminar.</summary>
    protected override void Voltar()
    {
        if (!Ocupado) base.Voltar();
    }

    [RelayCommand]
    private async Task FecharCaixa()
    {
        var sessao = Principal.Sessao;
        if (sessao is null || Ocupado) return;

        var texto = "Depois de fechar não dá para vender neste caixa até abrir de novo.";
        // Pedido pago cujas fichas não saíram (a impressora falhou): depois de fechar não dá mais para imprimir
        var naoImpressos = Sistema.Vendas.NaoImpressos(sessao.Id);
        if (naoImpressos > 0)
            texto = $"Atenção: {naoImpressos} pedido(s) pago(s) com fichas que não saíram na impressora. Depois de " +
                    "fechar, elas não podem mais ser impressas: imprima antes pelo Menu (Fichas não impressas).\n\n" + texto;
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

/// <param name="Saida">Dinheiro que saiu da gaveta (sangria ou devolução): o valor fica em vermelho.</param>
public sealed record MovimentoItem(string Hora, string Tipo, string Valor, string Motivo, bool Saida);

/// <summary>Sangria (tirar dinheiro) e suprimento (pôr dinheiro).</summary>
public sealed partial class SangriaViewModel(PrincipalViewModel principal) : PaginaViewModel(principal)
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EhSuprimento), nameof(TextoBotao))]
    private bool _ehSangria = true;

    [ObservableProperty] private string _motivo = "";
    [ObservableProperty] private string _dinheiroEmCaixa = "";
    [ObservableProperty] private bool _imprimirComprovante = true;
    [ObservableProperty] private string _depois = "";
    [ObservableProperty] private bool _depoisNegativo;
    [ObservableProperty] private List<LinhaValor> _composicao = new();
    private long _esperado;

    public EntradaValor Valor { get; } = new();
    public ObservableCollection<MovimentoItem> Movimentos { get; } = new();
    public bool EhSuprimento => !EhSangria;
    public string TextoBotao => EhSangria ? "Registrar sangria" : "Registrar suprimento";

    public static readonly string[] Notas = ["50", "100", "200", "500", "1000", "2000", "5000", "10000"];

    public override void AoAbrir()
    {
        Valor.PropertyChanged += (_, _) => AtualizarDepois();
        Atualizar();
    }

    partial void OnEhSangriaChanged(bool value) => AtualizarDepois();

    [RelayCommand]
    private void Tipo(string tipo) => EhSangria = tipo == "sangria";

    /// <summary>"Depois desta sangria ficam R$ X no caixa" (muda enquanto digita).</summary>
    private void AtualizarDepois()
    {
        if (Valor.Centavos == 0)
        {
            Depois = "";
            DepoisNegativo = false;
            return;
        }
        var depois = _esperado + (EhSangria ? -Valor.Centavos : Valor.Centavos);
        DepoisNegativo = depois < 0;
        Depois = depois < 0
            ? $"Não dá: no caixa só há {Dinheiro.Formatar(_esperado)}"
            : $"Depois {(EhSangria ? "da sangria" : "do suprimento")}: {Dinheiro.Formatar(depois)}";
    }

    /// <summary>Quando o último movimento foi registrado (para reconhecer o segundo toque de um toque duplo).</summary>
    private long _registradoEm = long.MinValue / 2;

    [RelayCommand]
    private async Task Salvar()
    {
        var sessao = Principal.Sessao;
        if (sessao is null) return;
        // Toque duplo no Registrar (sem comprovante): o primeiro registrou e zerou o valor; o segundo dava "Informe um
        // valor maior que zero" em vermelho e parecia que não tinha registrado (e a sangria era feita de novo).
        if (Valor.Centavos == 0 && Environment.TickCount64 - _registradoEm < 1000) return;
        try
        {
            var movimento = Sistema.Caixa.RegistrarMovimento(sessao,
                EhSangria ? TipoMovimento.Sangria : TipoMovimento.Suprimento, Valor.Centavos, Motivo);
            _registradoEm = Environment.TickCount64;
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
        var resumo = Sistema.Caixa.Resumo(sessao.Id);
        _esperado = resumo.DinheiroEsperado;
        DinheiroEmCaixa = Dinheiro.Formatar(_esperado);
        var composicao = new List<LinhaValor>
        {
            new("Troco inicial", Dinheiro.Formatar(resumo.Sessao.ValorAberturaCentavos)),
            new("+ Vendas em dinheiro", Dinheiro.Formatar(resumo.Total(FormaPagamento.Dinheiro))),
            new("+ Suprimentos", Dinheiro.Formatar(resumo.Suprimentos)),
            new("− Sangrias", Dinheiro.Formatar(resumo.Sangrias)),
        };
        if (resumo.Devolvido(FormaPagamento.Dinheiro) > 0)
            composicao.Add(new("− Devoluções", Dinheiro.Formatar(resumo.Devolvido(FormaPagamento.Dinheiro))));
        Composicao = composicao;
        AtualizarDepois();
        foreach (var m in Sistema.Caixa.Movimentos(sessao.Id))
        {
            Movimentos.Add(new MovimentoItem(m.CriadoEm.ToString("HH:mm", CultureInfo.InvariantCulture),
                m.NomeCurto, (m.Saida ? "− " : "+ ") + Dinheiro.Formatar(m.ValorCentavos), m.Motivo, m.Saida));
        }
    }
}
