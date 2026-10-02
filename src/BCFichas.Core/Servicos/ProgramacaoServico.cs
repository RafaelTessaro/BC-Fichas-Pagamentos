using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using BCFichas.Core.Dados;
using Microsoft.Data.Sqlite;

namespace BCFichas.Core.Servicos;

/// <summary>O que tem num arquivo de programação (mostrado antes de carregar).</summary>
public sealed record ResumoProgramacao(string Evento, int Produtos, int Abas, int Combos, DateTime SalvoEm, int Caixa,
    string Versao);

/// <summary>O que esta máquina tem guardado (mostrado na aba Máquina).</summary>
public sealed record SituacaoMaquina(int Produtos, int Abas, int Combos, int Pedidos, int Caixas);

/// <summary>
/// Programar várias máquinas e começar de novo, do jeito simples do sistema antigo (copiar o banco e trocar o
/// número do caixa; banco vazio para reprogramar):
/// <list type="bullet">
/// <item>Salvar a programação num arquivo (pendrive) e carregar em outra máquina, mudando só o número do caixa.</item>
/// <item>Começar outra festa: apaga as vendas e mantém produtos e configurações.</item>
/// <item>Deixar a máquina como nova: apaga tudo e mantém só o que é da máquina (impressora, número, senha).</item>
/// </list>
/// Antes de trocar ou apagar qualquer coisa, guarda uma cópia do banco em dados\backups.
/// </summary>
public sealed class ProgramacaoServico
{
    public const string Extensao = ".bcf";
    private const int Formato = 1;
    private const int CopiasGuardadas = 15;

    private readonly Banco _banco;
    private readonly ConfigServico _config;
    private readonly CatalogoServico _catalogo;
    private readonly string _pastaDados;
    private readonly Func<DateTime> _agora;

    public ProgramacaoServico(Banco banco, ConfigServico config, CatalogoServico catalogo, string pastaDados,
        Func<DateTime>? agora = null)
    {
        _banco = banco;
        _config = config;
        _catalogo = catalogo;
        _pastaDados = pastaDados;
        _agora = agora ?? (() => DateTime.Now);
    }

    public string PastaBackups => Path.Combine(_pastaDados, "backups");

    /// <summary>Nome sugerido do arquivo: "BCFichas - FESTA JUNINA.bcf".</summary>
    public string NomeArquivo()
    {
        var evento = new string(_config.Atual.NomeEvento.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim();
        return $"BCFichas - {(evento.Length == 0 ? "programacao" : evento)}{Extensao}";
    }

    public SituacaoMaquina Situacao() => new(
        (int)_banco.Escalar<long>("SELECT COUNT(*) FROM produtos"),
        (int)_banco.Escalar<long>("SELECT COUNT(*) FROM abas"),
        (int)_banco.Escalar<long>("SELECT COUNT(DISTINCT combo_id) FROM componentes_combo"),
        (int)_banco.Escalar<long>("SELECT COUNT(*) FROM pedidos WHERE teste = 0"),
        (int)_banco.Escalar<long>("SELECT COUNT(*) FROM sessoes WHERE teste = 0"));

    // ---------- Salvar e carregar a programação ----------

    /// <summary>Grava evento, configurações, abas, produtos, combos, fotos e logo num arquivo só.</summary>
    public void Salvar(string arquivo)
    {
        var config = _config.Atual;
        var produtos = _catalogo.Produtos();
        var arquivos = new Dictionary<string, string>(); // caminho no arquivo → caminho no disco

        string? Guardar(string? imagem)
        {
            var caminho = string.IsNullOrWhiteSpace(imagem) ? null
                : Path.IsPathRooted(imagem) ? imagem : Path.Combine(_pastaDados, imagem);
            if (caminho is null || !File.Exists(caminho)) return null;
            var nome = Path.IsPathRooted(imagem!)
                ? $"imagens/{Path.GetFileNameWithoutExtension(caminho)}-{arquivos.Count}{Path.GetExtension(caminho)}"
                : imagem!.Replace('\\', '/');
            arquivos[nome] = caminho;
            return nome;
        }

        var dados = new ArquivoProgramacao
        {
            Formato = Formato,
            Versao = typeof(ProgramacaoServico).Assembly.GetName().Version?.ToString(3) ?? "",
            SalvoEm = _agora(),
            Caixa = config.NumeroCaixa,
            Evento = config.NomeEvento,
            Config = JsonSerializer.Serialize(config, ConfigServico.Json),
            Logo = Guardar(config.Logo),
            Abas = _catalogo.Abas().Select(a => new AbaArquivo(a.Id, a.Nome, a.Ordem)).ToList(),
            Produtos = produtos.Select(p => new ProdutoArquivo
            {
                Id = p.Id, Nome = p.Nome, Detalhe = p.Detalhe, AbaId = p.AbaId, Posicao = p.Posicao,
                FichasPorUnidade = p.FichasPorUnidade, Custo = p.CustoCentavos, Preco = p.PrecoCentavos,
                ControlaEstoque = p.ControlaEstoque, Estoque = p.Estoque, Cor = p.Cor, Imagem = Guardar(p.Imagem),
                Ativo = p.Ativo,
                Componentes = p.Componentes.Select(c =>
                    new ComponenteArquivo(c.ProdutoId, c.Nome, c.Detalhe, c.Quantidade, c.ValorCentavos)).ToList(),
            }).ToList(),
        };

        var temporario = arquivo + ".tmp";
        using (var zip = ZipFile.Open(temporario, ZipArchiveMode.Create))
        {
            using (var escrita = new StreamWriter(zip.CreateEntry("programacao.json").Open()))
                escrita.Write(JsonSerializer.Serialize(dados, ConfigServico.Json));
            foreach (var (nome, caminho) in arquivos)
                zip.CreateEntryFromFile(caminho, nome, CompressionLevel.Fastest);
        }
        File.Move(temporario, arquivo, overwrite: true);
    }

    public ResumoProgramacao Resumo(string arquivo)
    {
        var dados = Ler(arquivo);
        return new ResumoProgramacao(dados.Evento, dados.Produtos.Count, dados.Abas.Count,
            dados.Produtos.Count(p => p.Componentes.Count > 0), dados.SalvoEm, dados.Caixa, dados.Versao);
    }

    /// <summary>
    /// Troca a programação desta máquina pela do arquivo (como copiar o banco de outra máquina): produtos, abas,
    /// combos, evento, ficha e senha vêm do arquivo; impressora, tela e Windows continuam os desta máquina. As
    /// vendas desta máquina são apagadas (a numeração volta para 1) e fica uma cópia de segurança.
    /// </summary>
    public void Carregar(string arquivo, int numeroCaixa)
    {
        if (numeroCaixa is < 1 or > 99) throw new ErroDeNegocio("O número do caixa vai de 1 a 99.");
        var dados = Ler(arquivo);
        ConferirCaixasFechados();
        CopiaDeSeguranca("antes-de-carregar");

        var importada = JsonSerializer.Deserialize<Configuracao>(dados.Config, ConfigServico.Json)
                        ?? throw new ErroDeNegocio("O arquivo de programação está com defeito.");
        importada.Atualizar();
        var config = importada.ComDadosDaMaquina(_config.Atual);
        config.NumeroCaixa = numeroCaixa;

        using var zip = ZipFile.OpenRead(arquivo);
        string? Extrair(string? nome)
        {
            if (nome is null || zip.GetEntry(nome) is not { } entrada) return null;
            var relativo = nome.Replace('/', Path.DirectorySeparatorChar);
            var destino = Path.GetFullPath(Path.Combine(_pastaDados, relativo));
            // Só dentro da pasta de dados (arquivo com nome estranho não escreve em outro lugar)
            var pasta = Path.GetFullPath(_pastaDados).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!destino.StartsWith(pasta, StringComparison.OrdinalIgnoreCase)) return null;
            Directory.CreateDirectory(Path.GetDirectoryName(destino)!);
            entrada.ExtractToFile(destino, overwrite: true);
            return relativo;
        }
        config.Logo = Extrair(dados.Logo);

        _banco.Transacao((c, t) =>
        {
            ApagarVendas(c, t);
            ApagarCatalogo(c, t);
            foreach (var aba in dados.Abas)
                Banco.Executar(c, t, "INSERT INTO abas (id, nome, ordem) VALUES ($id, $n, $o)",
                    ("$id", aba.Id), ("$n", aba.Nome), ("$o", aba.Ordem));
            if (dados.Abas.Count == 0) Banco.Executar(c, t, "INSERT INTO abas (nome, ordem) VALUES ('ITENS', 1)");
            var ids = dados.Produtos.Select(p => p.Id).ToHashSet();
            foreach (var p in dados.Produtos)
            {
                Banco.Executar(c, t, """
                    INSERT INTO produtos (id, nome, detalhe, aba_id, posicao, fichas_por_unidade, custo, preco,
                                          controla_estoque, estoque, cor, imagem, ativo)
                    VALUES ($id, $nome, $detalhe, $aba, $pos, $fichas, $custo, $preco, $ce, $est, $cor, $img, $ativo)
                    """,
                    ("$id", p.Id), ("$nome", p.Nome), ("$detalhe", p.Detalhe), ("$aba", p.AbaId), ("$pos", p.Posicao),
                    ("$fichas", p.FichasPorUnidade), ("$custo", p.Custo), ("$preco", p.Preco),
                    ("$ce", p.ControlaEstoque ? 1 : 0), ("$est", p.Estoque), ("$cor", p.Cor), ("$img", Extrair(p.Imagem)),
                    ("$ativo", p.Ativo ? 1 : 0));
                for (var i = 0; i < p.Componentes.Count; i++)
                {
                    var x = p.Componentes[i];
                    Banco.Executar(c, t, """
                        INSERT INTO componentes_combo (combo_id, produto_id, nome, detalhe, quantidade, valor, ordem)
                        VALUES ($c, $p, $n, $d, $q, $v, $o)
                        """,
                        ("$c", p.Id), ("$p", x.ProdutoId is { } id && ids.Contains(id) ? id : null), ("$n", x.Nome),
                        ("$d", x.Detalhe), ("$q", x.Quantidade), ("$v", x.Valor), ("$o", i + 1));
                }
            }
        });

        _config.Salvar(config);
        _catalogo.AvisarAlteracao();
    }

    // ---------- Começar de novo ----------

    /// <summary>
    /// Outra festa com o mesmo cardápio: apaga vendas, caixas, sangrias e devoluções; a numeração dos pedidos
    /// volta para 1. Produtos e configurações ficam.
    /// </summary>
    public void ApagarVendas()
    {
        ConferirCaixasFechados();
        CopiaDeSeguranca("antes-de-apagar-vendas");
        _banco.Transacao(ApagarVendas);
        _catalogo.AvisarAlteracao();
    }

    /// <summary>
    /// Como um banco vazio: apaga vendas, produtos, abas, combos, evento e ficha. Ficam o número do caixa, a
    /// impressora, as opções de tela e a senha master (com as telas travadas).
    /// </summary>
    public void DeixarComoNova()
    {
        ConferirCaixasFechados();
        CopiaDeSeguranca("antes-de-deixar-como-nova");
        _banco.Transacao((c, t) =>
        {
            ApagarVendas(c, t);
            ApagarCatalogo(c, t);
            Banco.Executar(c, t, "INSERT INTO abas (nome, ordem) VALUES ('ITENS', 1)");
        });
        var atual = _config.Atual;
        var nova = new Configuracao { VersaoConfig = Configuracao.VersaoAtual }.ComDadosDaMaquina(atual);
        nova.SenhaMaster = atual.SenhaMaster;
        nova.TelasProtegidas = atual.TelasProtegidas;
        _config.Salvar(nova);
        _catalogo.AvisarAlteracao();
    }

    /// <summary>
    /// Cópia do banco inteiro em dados\backups (guarda as últimas). Feita sozinha antes de apagar ou trocar
    /// alguma coisa; para voltar, o suporte copia o arquivo por cima do bcfichas.db com o programa fechado.
    /// </summary>
    public string CopiaDeSeguranca(string motivo)
    {
        Directory.CreateDirectory(PastaBackups);
        var nome = $"bcfichas-{_agora().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{motivo}.db";
        var destino = Path.Combine(PastaBackups, nome);
        if (File.Exists(destino)) File.Delete(destino);
        // VACUUM INTO faz uma cópia completa e consistente mesmo com o programa aberto (WAL)
        _banco.Executar("VACUUM INTO $d", ("$d", destino));
        foreach (var velho in Directory.GetFiles(PastaBackups, "bcfichas-*.db").OrderByDescending(f => f).Skip(CopiasGuardadas))
            File.Delete(velho);
        return destino;
    }

    private void ConferirCaixasFechados()
    {
        var abertos = _banco.Escalar<long>("SELECT COUNT(*) FROM sessoes WHERE fechada_em IS NULL AND teste = 0");
        if (abertos > 0) throw new ErroDeNegocio("Feche o caixa antes (Menu → Fechar caixa).");
        if (_banco.Escalar<long>("SELECT COUNT(*) FROM sessoes WHERE fechada_em IS NULL AND teste = 1") > 0)
            throw new ErroDeNegocio("Saia do modo teste antes.");
    }

    private static void ApagarVendas(SqliteConnection c, SqliteTransaction t) =>
        Banco.Executar(c, t, """
            DELETE FROM itens_devolucao;
            DELETE FROM devolucoes;
            DELETE FROM componentes_item;
            DELETE FROM itens_pedido;
            DELETE FROM pedidos;
            DELETE FROM movimentos;
            DELETE FROM sessoes;
            UPDATE contadores SET valor = 0 WHERE nome IN ('pedido', 'pedido_teste');
            """);

    private static void ApagarCatalogo(SqliteConnection c, SqliteTransaction t) =>
        Banco.Executar(c, t, """
            DELETE FROM componentes_combo;
            DELETE FROM produtos;
            DELETE FROM abas;
            """);

    private static ArquivoProgramacao Ler(string arquivo)
    {
        try
        {
            using var zip = ZipFile.OpenRead(arquivo);
            var entrada = zip.GetEntry("programacao.json") ?? throw new InvalidDataException();
            using var leitura = new StreamReader(entrada.Open());
            var dados = JsonSerializer.Deserialize<ArquivoProgramacao>(leitura.ReadToEnd(), ConfigServico.Json)
                        ?? throw new InvalidDataException();
            if (dados.Formato > Formato)
                throw new ErroDeNegocio("Esta programação foi salva por uma versão mais nova do BC Fichas. Atualize o programa.");
            return dados;
        }
        catch (Exception e) when (e is InvalidDataException or JsonException or IOException)
        {
            throw new ErroDeNegocio("Este arquivo não é uma programação do BC Fichas (ou está com defeito).");
        }
    }

    // Formato do arquivo (JSON dentro de um zip, junto com as fotos)
    private sealed class ArquivoProgramacao
    {
        public int Formato { get; set; }
        public string Versao { get; set; } = "";
        public DateTime SalvoEm { get; set; }
        public int Caixa { get; set; }
        public string Evento { get; set; } = "";
        public string Config { get; set; } = "{}";
        public string? Logo { get; set; }
        public List<AbaArquivo> Abas { get; set; } = new();
        public List<ProdutoArquivo> Produtos { get; set; } = new();
    }

    private sealed record AbaArquivo(long Id, string Nome, int Ordem);

    private sealed class ProdutoArquivo
    {
        public long Id { get; set; }
        public string Nome { get; set; } = "";
        public string Detalhe { get; set; } = "";
        public long AbaId { get; set; }
        public int Posicao { get; set; }
        public int FichasPorUnidade { get; set; } = 1;
        public long Custo { get; set; }
        public long Preco { get; set; }
        public bool ControlaEstoque { get; set; }
        public int Estoque { get; set; }
        public string Cor { get; set; } = "";
        public string? Imagem { get; set; }
        public bool Ativo { get; set; } = true;
        public List<ComponenteArquivo> Componentes { get; set; } = new();
    }

    private sealed record ComponenteArquivo(long? ProdutoId, string Nome, string Detalhe, int Quantidade, long Valor);
}
