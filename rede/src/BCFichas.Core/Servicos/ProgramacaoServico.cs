using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using BCFichas.Core.Dados;
using Microsoft.Data.Sqlite;

namespace BCFichas.Core.Servicos;

/// <summary>O que tem num arquivo de programação (mostrado antes de carregar).</summary>
public sealed record ResumoProgramacao(string Evento, int Produtos, int Abas, int Combos, DateTime SalvoEm, int Caixa,
    string Versao);

/// <summary>O que esta máquina tem guardado (mostrado na aba Máquina e antes de apagar ou restaurar).</summary>
/// <param name="EstoqueAVoltar">Unidades que as vendas tiraram do estoque e voltam para ele ao apagar as vendas.</param>
public sealed record SituacaoMaquina(int Produtos, int Abas, int Combos, int Pedidos, int PedidosTeste, int Caixas,
    bool CaixaAberto, int PedidosNoCaixaAberto, bool ModoTeste, int EstoqueAVoltar)
{
    /// <summary>Sem vendas, testes nem caixas guardados: a máquina vai "pura" para o cliente.</summary>
    public bool Pura => Pedidos == 0 && PedidosTeste == 0 && Caixas == 0;
}

/// <summary>Um backup achado na pasta do backup ou num pendrive.</summary>
public sealed record ProgramacaoEncontrada(string Arquivo, string Lugar, ResumoProgramacao Resumo);

/// <summary>Um arquivo .bcf achado que não dá para usar, e por quê (mostrado em vez de sumir da lista).</summary>
public sealed record ArquivoRecusado(string Arquivo, string Lugar, string Motivo);

/// <summary>Backups achados (o mais novo primeiro) e os arquivos .bcf que não deu para ler.</summary>
public sealed record ResultadoProcura(List<ProgramacaoEncontrada> Achados, List<ArquivoRecusado> Recusados);

/// <summary>
/// Programar várias máquinas e começar de novo, do jeito simples do sistema antigo (copiar o banco e trocar o
/// número do caixa; banco vazio para reprogramar):
/// <list type="bullet">
/// <item>Backup da programação num arquivo (pasta do backup e pendrive) e restaurar em outra máquina, mudando só o
/// número do caixa. O arquivo nunca leva vendas: quem restaura recebe a programação pura.</item>
/// <item>Apagar as vendas: deixa a máquina pura para o cliente (ou pronta para outra festa) e mantém produtos e
/// configurações.</item>
/// <item>Reprogramação para novo evento: zera a programação e mantém só o que é da máquina (impressora, número,
/// senha).</item>
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
    public string PastaImagens => Path.Combine(_pastaDados, "imagens");

    /// <summary>
    /// Caminho completo da pasta do backup. Aceita %USERPROFILE% e afins; um caminho sem a letra do disco vale a
    /// partir da pasta do programa (ele abre junto com o Windows numa pasta qualquer).
    /// </summary>
    public static string CaminhoDaPasta(string pasta) =>
        Path.GetFullPath(Environment.ExpandEnvironmentVariables(pasta.Trim()), AppContext.BaseDirectory);

    /// <summary>Nome sugerido do arquivo: "BCFichas - FESTA JUNINA.bcf".</summary>
    public string NomeArquivo()
    {
        var evento = new string(_config.Atual.NomeEvento.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim();
        return $"BCFichas - {(evento.Length == 0 ? "programacao" : evento)}{Extensao}";
    }

    public SituacaoMaquina Situacao() => _banco.Transacao((c, t) =>
    {
        // Tudo numa conexão só (no tablet, abrir a aba Máquina não pode demorar com muitas vendas guardadas)
        int Contar(string sql) => (int)Banco.Escalar<long>(c, t, sql);
        var pedidos = Banco.Consultar(c, t,
            "SELECT COALESCE(SUM(teste = 0), 0), COALESCE(SUM(teste = 1), 0) FROM pedidos",
            l => (Reais: (int)l.GetInt64(0), Teste: (int)l.GetInt64(1))).Single();
        return new SituacaoMaquina(
            Produtos: Contar("SELECT COUNT(*) FROM produtos"),
            Abas: Contar("SELECT COUNT(*) FROM abas"),
            Combos: Contar("SELECT COUNT(DISTINCT combo_id) FROM componentes_combo"),
            Pedidos: pedidos.Reais,
            PedidosTeste: pedidos.Teste,
            Caixas: Contar("SELECT COUNT(*) FROM sessoes"),
            CaixaAberto: Contar("SELECT COUNT(*) FROM sessoes WHERE fechada_em IS NULL AND teste = 0") > 0,
            PedidosNoCaixaAberto: Contar("""
                SELECT COUNT(*) FROM pedidos p JOIN sessoes s ON s.id = p.sessao_id
                WHERE s.fechada_em IS NULL AND s.teste = 0
                """),
            ModoTeste: Contar("SELECT COUNT(*) FROM sessoes WHERE fechada_em IS NULL AND teste = 1") > 0,
            EstoqueAVoltar: EstoqueVendido(c, t).Values.Sum());
    });

    /// <summary>
    /// Backups (.bcf) achados direto nas pastas (pasta do backup, pendrives), do mais novo para o mais velho. Os
    /// que não dá para ler (cópia pela metade, versão mais nova) voltam separados, com o motivo.
    /// </summary>
    public ResultadoProcura Procurar(IEnumerable<(string Pasta, string Lugar)> lugares)
    {
        var achados = new List<ProgramacaoEncontrada>();
        var recusados = new List<ArquivoRecusado>();
        var vistos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (pasta, lugar) in lugares)
        {
            string[] arquivos;
            try
            {
                arquivos = Directory.Exists(pasta) ? Directory.GetFiles(pasta, "*" + Extensao) : [];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                continue;
            }
            // No Windows "*.bcf" também acha "*.bcfx": confere a extensão inteira.
            foreach (var arquivo in arquivos.Where(a => a.EndsWith(Extensao, StringComparison.OrdinalIgnoreCase)))
            {
                if (!vistos.Add(Path.GetFullPath(arquivo))) continue;
                try
                {
                    achados.Add(new ProgramacaoEncontrada(arquivo, lugar, Resumo(arquivo)));
                }
                catch (ErroDeNegocio e)
                {
                    recusados.Add(new ArquivoRecusado(arquivo, lugar, e.Message));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    recusados.Add(new ArquivoRecusado(arquivo, lugar, ArquivoIlegivel));
                }
            }
        }
        return new ResultadoProcura(achados.OrderByDescending(a => a.Resumo.SalvoEm).ToList(), recusados);
    }

    private const string ArquivoIlegivel =
        "Não deu para ler este arquivo: ele não é um backup do BC Fichas, está com defeito ou ainda está sendo copiado.";

    // ---------- Salvar e carregar a programação ----------

    /// <summary>Grava evento, configurações, abas, produtos, combos, fotos e logo num arquivo só.</summary>
    public void Salvar(string arquivo)
    {
        var config = _config.Atual;
        var produtos = _catalogo.Produtos();
        // O backup sai "puro": com o estoque de antes das vendas desta máquina, como se elas fossem apagadas.
        var vendido = _banco.Transacao(EstoqueVendido);
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
            Abas = _catalogo.Abas().Select(a => new AbaArquivo(a.Id, a.Nome, a.Ordem, a.Colunas, a.Linhas)).ToList(),
            Produtos = produtos.Select(p => new ProdutoArquivo
            {
                Id = p.Id, Nome = p.Nome, Detalhe = p.Detalhe, AbaId = p.AbaId, Posicao = p.Posicao,
                FichasPorUnidade = p.FichasPorUnidade, Custo = p.CustoCentavos, Preco = p.PrecoCentavos,
                ControlaEstoque = p.ControlaEstoque,
                Estoque = p.ControlaEstoque ? p.Estoque + vendido.GetValueOrDefault(p.Id) : p.Estoque,
                Cor = p.Cor, Imagem = Guardar(p.Imagem),
                Ativo = p.Ativo,
                Componentes = p.Componentes.Select(c =>
                    new ComponenteArquivo(c.ProdutoId, c.Nome, c.Detalhe, c.Quantidade, c.ValorCentavos)).ToList(),
            }).ToList(),
        };

        // Grava num arquivo temporário e só no fim troca pelo de verdade (um backup pela metade nunca fica com o
        // nome certo). Um temporário que sobrou de uma vez que deu errado é substituído.
        var temporario = arquivo + ".tmp";
        try
        {
            using (var zip = new ZipArchive(new FileStream(temporario, FileMode.Create), ZipArchiveMode.Create))
            {
                using (var escrita = new StreamWriter(zip.CreateEntry("programacao.json").Open()))
                    escrita.Write(JsonSerializer.Serialize(dados, ConfigServico.Json));
                foreach (var (nome, caminho) in arquivos)
                    zip.CreateEntryFromFile(caminho, nome, CompressionLevel.Fastest);
            }
            File.Move(temporario, arquivo, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temporario);
            }
            catch (Exception)
            {
                // fica para a próxima vez (FileMode.Create grava por cima)
            }
            throw;
        }
    }

    public ResumoProgramacao Resumo(string arquivo)
    {
        var dados = Ler(arquivo);
        return new ResumoProgramacao(dados.Evento, dados.Produtos.Count, dados.Abas.Count,
            dados.Produtos.Count(p => p.Componentes.Count > 0), dados.SalvoEm, dados.Caixa, dados.Versao);
    }

    /// <summary>
    /// Troca a programação desta máquina pela do arquivo (como copiar o banco de outra máquina): produtos, abas,
    /// combos, evento, ficha e senha vêm do arquivo; impressora, tela, pastas e Windows continuam os desta máquina.
    /// As vendas, testes e caixas desta máquina (até o aberto) são apagados, a numeração volta para 1 e fica uma
    /// cópia de segurança.
    /// </summary>
    public void Carregar(string arquivo, int numeroCaixa)
    {
        if (numeroCaixa is < 1 or > 99) throw new ErroDeNegocio("O número do caixa vai de 1 a 99.");
        var dados = Ler(arquivo);
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
            {
                // Backup de antes da 3.10: a grade era a das configurações, igual para todas as abas
                var colunas = aba.Colunas ?? Math.Clamp(importada.Colunas, 1, CatalogoServico.MaximoColunas);
                var linhas = aba.Linhas ?? Math.Clamp(importada.Linhas, 1, CatalogoServico.MaximoLinhas);
                Banco.Executar(c, t, "INSERT INTO abas (id, nome, ordem, colunas, linhas) VALUES ($id, $n, $o, $c, $l)",
                    ("$id", aba.Id), ("$n", aba.Nome), ("$o", aba.Ordem), ("$c", colunas), ("$l", linhas));
            }
            if (dados.Abas.Count == 0) Banco.Executar(c, t, "INSERT INTO abas (nome, ordem) VALUES ('ITENS', 1)");
            // Primeiro todos os produtos, depois as fichas dos combos: o combo pode vir antes do produto das fichas.
            var ids = dados.Produtos.Select(p => p.Id).ToHashSet();
            foreach (var p in dados.Produtos)
                Banco.Executar(c, t, """
                    INSERT INTO produtos (id, nome, detalhe, aba_id, posicao, fichas_por_unidade, custo, preco,
                                          controla_estoque, estoque, cor, imagem, ativo)
                    VALUES ($id, $nome, $detalhe, $aba, $pos, $fichas, $custo, $preco, $ce, $est, $cor, $img, $ativo)
                    """,
                    ("$id", p.Id), ("$nome", p.Nome), ("$detalhe", p.Detalhe), ("$aba", p.AbaId), ("$pos", p.Posicao),
                    ("$fichas", p.FichasPorUnidade), ("$custo", p.Custo), ("$preco", p.Preco),
                    ("$ce", p.ControlaEstoque ? 1 : 0), ("$est", p.Estoque), ("$cor", p.Cor), ("$img", Extrair(p.Imagem)),
                    ("$ativo", p.Ativo ? 1 : 0));
            foreach (var p in dados.Produtos)
            {
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
            // Na mesma transação: os produtos do backup nunca ficam com o evento e o número de caixa de antes
            ConfigServico.Gravar(c, t, config);
        });

        _config.Aplicar(config);
        _catalogo.AvisarAlteracao();
        LimparArquivosSemUso();
        Compactar();
    }

    // ---------- Começar de novo ----------

    /// <summary>
    /// Deixa a máquina pura para o cliente (ou pronta para outra festa com o mesmo cardápio): apaga vendas, testes,
    /// caixas (até o que estiver aberto), sangrias e devoluções; a numeração dos pedidos volta para 1. Produtos e
    /// configurações ficam.
    /// </summary>
    /// <param name="devolverEstoque">
    /// O que as vendas tiraram do estoque volta para ele (máquina pura para o cliente). Numa festa nova com o que
    /// sobrou da anterior, o estoque fica como está.
    /// </param>
    public void ApagarVendas(bool devolverEstoque = true)
    {
        // Máquina já pura: nada a perder, e a cópia empurraria para fora uma cópia que ainda tem vendas.
        if (!Situacao().Pura) CopiaDeSeguranca("antes-de-apagar-vendas");
        _banco.Transacao((c, t) =>
        {
            if (devolverEstoque) DevolverAoEstoque(c, t);
            ApagarVendas(c, t);
        });
        _catalogo.AvisarAlteracao();
        LimparArquivosSemUso();
        Compactar();
    }

    /// <summary>
    /// Reprogramação para um novo evento (como o banco vazio do sistema antigo): apaga vendas, produtos, abas, combos,
    /// evento e ficha. Ficam o número do caixa, a impressora, as opções de tela, as pastas e a senha master (com as
    /// telas travadas).
    /// </summary>
    public void ZerarProgramacao()
    {
        CopiaDeSeguranca("antes-de-zerar-programacao");
        var atual = _config.Atual;
        var nova = new Configuracao { VersaoConfig = Configuracao.VersaoAtual }.ComDadosDaMaquina(atual);
        nova.SenhaMaster = atual.SenhaMaster;
        nova.TelasProtegidas = atual.TelasProtegidas;
        // O painel no celular é do kit (roteador e máquinas), não do evento: continua ligado e com o mesmo PIN
        nova.PainelAtivo = atual.PainelAtivo;
        nova.PainelPin = atual.PainelPin;
        nova.PainelMaquinas = atual.PainelMaquinas;
        _banco.Transacao((c, t) =>
        {
            ApagarVendas(c, t);
            ApagarCatalogo(c, t);
            Banco.Executar(c, t, "INSERT INTO abas (nome, ordem) VALUES ('ITENS', 1)");
            ConfigServico.Gravar(c, t, nova);
        });
        _config.Aplicar(nova);
        _catalogo.AvisarAlteracao();
        LimparArquivosSemUso(); // sem produtos e sem logotipo: as fotos e os logos do evento anterior saem todos
        Compactar();
    }

    /// <summary>
    /// Depois de apagar muita coisa: o banco devolve o espaço vazio ao disco (sem isso, o arquivo continua do
    /// tamanho do maior evento que já guardou) e o arquivo -wal volta a zero.
    /// </summary>
    private void Compactar()
    {
        try
        {
            _banco.Executar("VACUUM");
            _banco.Executar("PRAGMA wal_checkpoint(TRUNCATE)");
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // Banco ocupado agora (outra leitura aberta): fica para a próxima vez.
        }
    }

    /// <summary>
    /// Apaga da pasta de dados as imagens que nenhum produto nem o logotipo usam: foto trocada, produto excluído,
    /// logotipo antigo, programação de outro evento. Feito ao abrir o programa, ao restaurar e ao zerar a
    /// programação (com uma tela de produtos aberta, uma foto escolhida e ainda não salva sumiria). Devolve
    /// quantas apagou.
    /// </summary>
    public int LimparImagensSemUso()
    {
        if (!Directory.Exists(PastaImagens) || _config.ComDefeito) return 0;
        var usadas = _catalogo.Produtos().Select(p => p.Imagem).Append(_config.Atual.Logo)
            .Where(i => !string.IsNullOrWhiteSpace(i))
            .Select(i => Path.GetFullPath(Path.Combine(_pastaDados, i!)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var apagadas = 0;
        foreach (var arquivo in Directory.GetFiles(PastaImagens, "*", SearchOption.AllDirectories))
        {
            if (usadas.Contains(Path.GetFullPath(arquivo))) continue;
            try
            {
                File.Delete(arquivo);
                apagadas++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Em uso agora: sai da próxima vez.
            }
        }
        return apagadas;
    }

    /// <summary>
    /// Ao abrir o programa: a impressora "Salvar em arquivo" (teste sem impressora) grava uma imagem por ficha na
    /// pasta padrão; ficam só as últimas, para uma máquina esquecida nesse modo não encher o disco.
    /// </summary>
    public void LimparFichasSalvasAntigas(int manter = 300)
    {
        var impressoes = Path.Combine(_pastaDados, Impressao.ServicoImpressao.PastaArquivoPadrao);
        if (!Directory.Exists(impressoes)) return;
        foreach (var arquivo in new DirectoryInfo(impressoes).GetFiles().OrderByDescending(f => f.LastWriteTimeUtc).Skip(manter))
        {
            try
            {
                arquivo.Delete();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Em uso agora: sai da próxima vez.
            }
        }
    }

    /// <summary>
    /// Depois de apagar vendas, restaurar ou zerar: tira as imagens sem uso e as fichas que a impressora "Salvar
    /// em arquivo" (teste sem impressora) guardou na pasta padrão.
    /// </summary>
    private void LimparArquivosSemUso()
    {
        try
        {
            LimparImagensSemUso();
            var impressoes = Path.Combine(_pastaDados, Impressao.ServicoImpressao.PastaArquivoPadrao);
            if (!Directory.Exists(impressoes)) return;
            foreach (var arquivo in Directory.GetFiles(impressoes))
            {
                try
                {
                    File.Delete(arquivo);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Em uso agora: sai da próxima vez.
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Pasta sem permissão: a operação (que já foi feita) não pode parecer que falhou por causa da limpeza.
        }
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
        // A que acabou de ser feita fica sempre, mesmo se o relógio do tablet voltou no tempo e o nome dela é "velho"
        foreach (var velho in Directory.GetFiles(PastaBackups, "bcfichas-*.db")
                     .Where(f => !string.Equals(Path.GetFullPath(f), Path.GetFullPath(destino), StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(f => f).Skip(CopiasGuardadas - 1))
            File.Delete(velho);
        return destino;
    }

    /// <summary>
    /// Quanto as vendas tiraram de verdade do estoque de cada produto (o que ficou guardado na venda, menos o que as
    /// devoluções já puseram de volta). No combo, o próprio combo e os produtos das fichas. Vendas de teste e não
    /// pagas não tiraram nada, e um estoque digitado de novo zera o que veio antes.
    /// </summary>
    private static Dictionary<long, int> EstoqueVendido(SqliteConnection c, SqliteTransaction t) =>
        Banco.Consultar(c, t, """
            SELECT v.produto_id, SUM(v.volta) FROM (
                SELECT i.produto_id, MAX(i.baixado - (SELECT COALESCE(SUM(d.quantidade), 0) FROM itens_devolucao d
                                                      WHERE d.item_id = i.id AND d.componente_id IS NULL), 0) AS volta
                FROM itens_pedido i WHERE i.baixado > 0 AND i.produto_id IS NOT NULL
                UNION ALL
                SELECT x.produto_id, MAX(x.baixado - (SELECT COALESCE(SUM(d.quantidade), 0) FROM itens_devolucao d
                                                      WHERE d.componente_id = x.id), 0)
                FROM componentes_item x WHERE x.baixado > 0 AND x.produto_id IS NOT NULL
            ) v JOIN produtos p ON p.id = v.produto_id
            WHERE p.controla_estoque = 1
            GROUP BY v.produto_id HAVING SUM(v.volta) > 0
            """, l => (Id: l.GetInt64(0), Quantidade: (int)l.GetInt64(1)))
            .ToDictionary(x => x.Id, x => x.Quantidade);

    /// <summary>O que as vendas tiraram do estoque volta para ele, como se elas não tivessem acontecido.</summary>
    private static void DevolverAoEstoque(SqliteConnection c, SqliteTransaction t)
    {
        foreach (var (id, quantidade) in EstoqueVendido(c, t))
            Banco.Executar(c, t, "UPDATE produtos SET estoque = estoque + $q WHERE id = $id",
                ("$q", quantidade), ("$id", id));
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
            throw new ErroDeNegocio(ArquivoIlegivel);
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

    /// <summary>Colunas e linhas vazias: backup de antes da 3.10 (a grade estava nas configurações).</summary>
    private sealed record AbaArquivo(long Id, string Nome, int Ordem, int? Colunas = null, int? Linhas = null);

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
