using BCFichas.Core;
using BCFichas.Core.Servicos;

namespace BCFichas.Tests.Core;

/// <summary>
/// Catálogo fixo dos testes (3 abas, 15 produtos), separado dos exemplos que o programa cria,
/// assim os testes não quebram quando os exemplos mudam.
/// </summary>
public static class CatalogoDeTeste
{
    public static void Criar(CatalogoServico catalogo)
    {
        var abas = catalogo.Abas();
        var comidas = abas.Count > 0 ? abas[0] : new Aba();
        comidas.Nome = "Comidas";
        comidas = catalogo.SalvarAba(comidas);
        var bebidas = catalogo.SalvarAba(new Aba { Nome = "Bebidas" });
        var doces = catalogo.SalvarAba(new Aba { Nome = "Doces" });

        (Aba aba, string nome, string detalhe, long preco, string cor)[] produtos =
        [
            (comidas, "Pastel", "Carne ou queijo", 1000, "#E8590C"),
            (comidas, "Espetinho", "", 1200, "#C92A2A"),
            (comidas, "Cachorro-quente", "", 1000, "#D9480F"),
            (comidas, "Porção de batata", "", 2000, "#F08C00"),
            (comidas, "Pizza (fatia)", "", 800, "#E67700"),
            (comidas, "Caldo", "Feijão ou mandioca", 1200, "#A61E4D"),
            (bebidas, "Refrigerante", "Lata", 600, "#1971C2"),
            (bebidas, "Água", "Com ou sem gás", 400, "#1098AD"),
            (bebidas, "Suco", "", 700, "#2B8A3E"),
            (bebidas, "Cerveja", "Lata", 800, "#E8B200"),
            (bebidas, "Quentão", "", 700, "#862E9C"),
            (doces, "Bolo", "Fatia", 500, "#9C36B5"),
            (doces, "Pipoca", "", 500, "#F59F00"),
            (doces, "Algodão doce", "", 600, "#D6336C"),
            (doces, "Paçoca", "", 200, "#A0522D"),
        ];

        var posicoes = new Dictionary<long, int>();
        foreach (var (aba, nome, detalhe, preco, cor) in produtos)
        {
            posicoes[aba.Id] = posicoes.GetValueOrDefault(aba.Id) + 1;
            catalogo.SalvarProduto(new Produto
            {
                Nome = nome,
                Detalhe = detalhe,
                AbaId = aba.Id,
                Posicao = posicoes[aba.Id],
                PrecoCentavos = preco,
                Cor = cor,
            }, 36);
        }
    }
}
