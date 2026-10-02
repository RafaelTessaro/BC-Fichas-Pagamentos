namespace BCFichas.Core;

/// <summary>Erro com mensagem pronta para mostrar ao operador.</summary>
public sealed class ErroDeNegocio(string mensagem) : Exception(mensagem);
