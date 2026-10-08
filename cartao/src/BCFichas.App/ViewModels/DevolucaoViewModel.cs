using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BCFichas.Core;
using BCFichas.Core.Impressao;

namespace BCFichas.App.ViewModels;

public sealed record DevolucaoFeita(string Hora, string Valor);

/// <summary>
/// Devolução de fichas (só em dinheiro): o cliente traz as fichas que não usou, o caixa soma o valor delas e
/// digita o total. O dinheiro sai da gaveta como uma sangria e aparece no fechamento como devolução. Não precisa
/// do número da venda.
/// </summary>
public sealed partial class DevolucaoViewModel(PrincipalViewModel principal) : PaginaViewModel(principal)
{
    public EntradaValor Valor { get; } = new();
    public ObservableCollection<DevolucaoFeita> Feitas { get; } = new();

    [ObservableProperty] private string _dinheiroEmCaixa = "";
    [ObservableProperty] private string _totalDevolvido = "";
    [ObservableProperty] private string _depois = "";
    [ObservableProperty] private bool _depoisNegativo;
    [ObservableProperty] private string _textoBotao = "Digite o valor das fichas";
    [ObservableProperty] private bool _podeDevolver;
    [ObservableProperty] private bool _imprimirComprovante = true;
    [ObservableProperty] private bool _ocupado;
    private long _esperado;

    public override void AoAbrir()
    {
        Valor.PropertyChanged += (_, _) => AtualizarValor();
        Atualizar();
    }

    [RelayCommand]
    private async Task Registrar()
    {
        var sessao = Principal.Sessao;
        if (sessao is null || Ocupado) return;
        Ocupado = true;
        try
        {
            var devolucao = Sistema.Caixa.RegistrarMovimento(sessao, TipoMovimento.Devolucao, Valor.Centavos, "");
            Principal.MostrarAviso($"Devolução de {Dinheiro.Formatar(devolucao.ValorCentavos)} registrada.");
            Valor.Centavos = 0;
            Atualizar();
            if (ImprimirComprovante)
            {
                var config = Principal.Config;
                var noCaixa = _esperado;
                var teste = sessao.Teste;
                await Principal.ImprimirAsync(() =>
                    Sistema.Impressao.Documento(Relatorios.Movimento(devolucao, config, noCaixa, teste), "Comprovante impresso"));
            }
        }
        catch (ErroDeNegocio e)
        {
            Principal.MostrarAviso(e.Message, erro: true);
        }
        finally
        {
            Ocupado = false;
        }
    }

    /// <summary>"Depois da devolução ficam R$ X no caixa" (muda enquanto digita) e o texto do botão.</summary>
    private void AtualizarValor()
    {
        TextoBotao = Valor.Centavos > 0 ? $"Devolver {Valor.Texto} ao cliente" : "Digite o valor das fichas";
        if (Valor.Centavos == 0)
        {
            Depois = "";
            DepoisNegativo = false;
            PodeDevolver = false;
            return;
        }
        var depois = _esperado - Valor.Centavos;
        DepoisNegativo = depois < 0;
        PodeDevolver = !DepoisNegativo;
        Depois = depois < 0
            ? $"Não dá: no caixa só há {Dinheiro.Formatar(_esperado)}"
            : $"Depois da devolução: {Dinheiro.Formatar(depois)} no caixa";
    }

    private void Atualizar()
    {
        Feitas.Clear();
        var sessao = Principal.Sessao;
        if (sessao is not null)
        {
            var resumo = Sistema.Caixa.Resumo(sessao.Id);
            _esperado = resumo.DinheiroEsperado;
            TotalDevolvido = Dinheiro.Formatar(resumo.Devolvido(FormaPagamento.Dinheiro));
            foreach (var m in Sistema.Caixa.Movimentos(sessao.Id).Where(m => m.Tipo == TipoMovimento.Devolucao))
                Feitas.Add(new DevolucaoFeita(m.CriadoEm.ToString("HH:mm", CultureInfo.InvariantCulture),
                    "− " + Dinheiro.Formatar(m.ValorCentavos)));
        }
        DinheiroEmCaixa = Dinheiro.Formatar(_esperado);
        AtualizarValor();
    }
}
