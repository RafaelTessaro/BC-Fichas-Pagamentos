using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BCFichas.Core;

namespace BCFichas.App.ViewModels;

/// <summary>
/// Tablet não liberado (o programa foi copiado de outra máquina, ou é um tablet novo): o BC Fichas só abre depois
/// da senha técnica da BC Fichas. Liberado uma vez, não pede mais nesta máquina.
/// </summary>
public sealed partial class LiberacaoViewModel(PrincipalViewModel principal) : PaginaViewModel(principal)
{
    [ObservableProperty] private string _senhaDigitada = "";
    [ObservableProperty] private string _erro = "";
    [ObservableProperty] private bool _conferindo;

    [RelayCommand]
    private async Task Liberar()
    {
        if (Conferindo) return;
        var digitada = SenhaDigitada;
        Conferindo = true;
        var certa = await Task.Run(() => Principal.SenhaTecnica.Confere(digitada));
        Conferindo = false;
        SenhaDigitada = "";
        if (!certa)
        {
            Erro = "Senha técnica errada.";
            return;
        }
        try
        {
            Principal.Liberacao.Liberar();
        }
        catch (ErroDeNegocio e)
        {
            Erro = e.Message;
            return;
        }
        Erro = "";
        Principal.TecladoVisivel = false;
        Principal.Iniciar();
        Principal.MostrarAviso("Tablet liberado.");
    }

    [RelayCommand]
    private void Sair() => Principal.Sair();
}
