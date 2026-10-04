using System.Globalization;
using Microsoft.Data.Sqlite;

namespace BCFichas.Core.Dados;

/// <summary>
/// Banco SQLite local (um arquivo). Leve, não precisa de servidor e aguenta queda de energia.
/// </summary>
public sealed class Banco
{
    private const string FormatoData = "yyyy-MM-dd HH:mm:ss";

    private readonly string _conexao;

    public string Caminho { get; }

    public Banco(string caminho)
    {
        Caminho = caminho;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(caminho))!);
        _conexao = new SqliteConnectionStringBuilder
        {
            DataSource = caminho,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
        }.ToString();
        Migrar();
    }

    public bool Novo { get; private set; }

    public SqliteConnection Abrir()
    {
        var conexao = new SqliteConnection(_conexao);
        conexao.Open();
        using var cmd = conexao.CreateCommand();
        // journal_size_limit: depois de uma operação grande (apagar as vendas, restaurar), o arquivo -wal volta a
        // no máximo 4 MB em vez de ficar do tamanho dela.
        cmd.CommandText = "PRAGMA foreign_keys = ON; PRAGMA synchronous = FULL; PRAGMA journal_size_limit = 4194304;";
        cmd.ExecuteNonQuery();
        return conexao;
    }

    public int Executar(string sql, params (string Nome, object? Valor)[] parametros)
    {
        using var conexao = Abrir();
        return Executar(conexao, null, sql, parametros);
    }

    public static int Executar(SqliteConnection conexao, SqliteTransaction? transacao, string sql,
        params (string Nome, object? Valor)[] parametros)
    {
        using var cmd = Comando(conexao, transacao, sql, parametros);
        return cmd.ExecuteNonQuery();
    }

    public T? Escalar<T>(string sql, params (string Nome, object? Valor)[] parametros)
    {
        using var conexao = Abrir();
        return Escalar<T>(conexao, null, sql, parametros);
    }

    public static T? Escalar<T>(SqliteConnection conexao, SqliteTransaction? transacao, string sql,
        params (string Nome, object? Valor)[] parametros)
    {
        using var cmd = Comando(conexao, transacao, sql, parametros);
        var valor = cmd.ExecuteScalar();
        if (valor is null || valor is DBNull) return default;
        var tipo = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)Convert.ChangeType(valor, tipo, CultureInfo.InvariantCulture);
    }

    public List<T> Consultar<T>(string sql, Func<SqliteDataReader, T> mapear,
        params (string Nome, object? Valor)[] parametros)
    {
        using var conexao = Abrir();
        return Consultar(conexao, null, sql, mapear, parametros);
    }

    public static List<T> Consultar<T>(SqliteConnection conexao, SqliteTransaction? transacao, string sql,
        Func<SqliteDataReader, T> mapear, params (string Nome, object? Valor)[] parametros)
    {
        using var cmd = Comando(conexao, transacao, sql, parametros);
        using var leitor = cmd.ExecuteReader();
        var lista = new List<T>();
        while (leitor.Read()) lista.Add(mapear(leitor));
        return lista;
    }

    public T Transacao<T>(Func<SqliteConnection, SqliteTransaction, T> acao)
    {
        using var conexao = Abrir();
        using var transacao = conexao.BeginTransaction();
        var resultado = acao(conexao, transacao);
        transacao.Commit();
        return resultado;
    }

    public void Transacao(Action<SqliteConnection, SqliteTransaction> acao) =>
        Transacao<object?>((c, t) => { acao(c, t); return null; });

    public static string Data(DateTime data) => data.ToString(FormatoData, CultureInfo.InvariantCulture);

    public static DateTime LerData(SqliteDataReader leitor, string coluna) =>
        DateTime.ParseExact(leitor.GetString(leitor.GetOrdinal(coluna)), FormatoData, CultureInfo.InvariantCulture);

    public static DateTime? LerDataOpcional(SqliteDataReader leitor, string coluna)
    {
        var i = leitor.GetOrdinal(coluna);
        return leitor.IsDBNull(i)
            ? null
            : DateTime.ParseExact(leitor.GetString(i), FormatoData, CultureInfo.InvariantCulture);
    }

    public static string? LerTextoOpcional(SqliteDataReader leitor, string coluna)
    {
        var i = leitor.GetOrdinal(coluna);
        return leitor.IsDBNull(i) ? null : leitor.GetString(i);
    }

    public static long? LerLongOpcional(SqliteDataReader leitor, string coluna)
    {
        var i = leitor.GetOrdinal(coluna);
        return leitor.IsDBNull(i) ? null : leitor.GetInt64(i);
    }

    private static SqliteCommand Comando(SqliteConnection conexao, SqliteTransaction? transacao, string sql,
        (string Nome, object? Valor)[] parametros)
    {
        var cmd = conexao.CreateCommand();
        cmd.Transaction = transacao;
        cmd.CommandText = sql;
        foreach (var (nome, valor) in parametros)
            cmd.Parameters.AddWithValue(nome, valor ?? DBNull.Value);
        return cmd;
    }

    private void Migrar()
    {
        using var conexao = Abrir();
        using (var wal = conexao.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode = WAL;";
            wal.ExecuteNonQuery();
        }

        Executar(conexao, null, "CREATE TABLE IF NOT EXISTS versao (v INTEGER NOT NULL)");
        var versao = Escalar<long?>(conexao, null, "SELECT MAX(v) FROM versao") ?? 0;
        Novo = versao == 0;

        if (versao < 1)
        {
            using var t = conexao.BeginTransaction();
            Executar(conexao, t, """
                CREATE TABLE config (
                    chave TEXT PRIMARY KEY,
                    valor TEXT NOT NULL
                );
                CREATE TABLE contadores (
                    nome TEXT PRIMARY KEY,
                    valor INTEGER NOT NULL
                );
                INSERT INTO contadores (nome, valor) VALUES ('pedido', 0);
                CREATE TABLE abas (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    nome TEXT NOT NULL,
                    ordem INTEGER NOT NULL
                );
                CREATE TABLE produtos (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    nome TEXT NOT NULL,
                    detalhe TEXT NOT NULL DEFAULT '',
                    aba_id INTEGER NOT NULL REFERENCES abas(id),
                    posicao INTEGER NOT NULL,
                    fichas_por_unidade INTEGER NOT NULL DEFAULT 1,
                    custo INTEGER NOT NULL DEFAULT 0,
                    preco INTEGER NOT NULL,
                    controla_estoque INTEGER NOT NULL DEFAULT 0,
                    estoque INTEGER NOT NULL DEFAULT 0,
                    cor TEXT NOT NULL,
                    imagem TEXT,
                    ativo INTEGER NOT NULL DEFAULT 1
                );
                CREATE TABLE sessoes (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    caixa INTEGER NOT NULL,
                    operador TEXT NOT NULL,
                    aberta_em TEXT NOT NULL,
                    valor_abertura INTEGER NOT NULL,
                    fechada_em TEXT,
                    valor_contado INTEGER
                );
                CREATE TABLE pedidos (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    numero INTEGER NOT NULL,
                    sessao_id INTEGER NOT NULL REFERENCES sessoes(id),
                    caixa INTEGER NOT NULL,
                    criado_em TEXT NOT NULL,
                    total INTEGER NOT NULL,
                    forma INTEGER NOT NULL,
                    recebido INTEGER NOT NULL DEFAULT 0,
                    troco INTEGER NOT NULL DEFAULT 0,
                    status INTEGER NOT NULL,
                    autorizacao TEXT,
                    impressoes INTEGER NOT NULL DEFAULT 0
                );
                CREATE INDEX ix_pedidos_sessao ON pedidos (sessao_id);
                CREATE TABLE itens_pedido (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    pedido_id INTEGER NOT NULL REFERENCES pedidos(id),
                    produto_id INTEGER,
                    nome TEXT NOT NULL,
                    detalhe TEXT NOT NULL DEFAULT '',
                    preco INTEGER NOT NULL,
                    quantidade INTEGER NOT NULL,
                    fichas_por_unidade INTEGER NOT NULL DEFAULT 1
                );
                CREATE INDEX ix_itens_pedido ON itens_pedido (pedido_id);
                CREATE TABLE movimentos (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    sessao_id INTEGER NOT NULL REFERENCES sessoes(id),
                    caixa INTEGER NOT NULL,
                    tipo INTEGER NOT NULL,
                    valor INTEGER NOT NULL,
                    motivo TEXT NOT NULL DEFAULT '',
                    usuario TEXT NOT NULL DEFAULT '',
                    criado_em TEXT NOT NULL
                );
                CREATE INDEX ix_movimentos_sessao ON movimentos (sessao_id);
                INSERT INTO versao (v) VALUES (1);
                """);
            t.Commit();
        }

        // Versão 3.2: modo teste e devolução de fichas.
        if (versao < 2)
        {
            using var t = conexao.BeginTransaction();
            Executar(conexao, t, """
                ALTER TABLE sessoes ADD COLUMN teste INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE pedidos ADD COLUMN teste INTEGER NOT NULL DEFAULT 0;
                CREATE INDEX ix_pedidos_numero ON pedidos (numero);
                INSERT OR IGNORE INTO contadores (nome, valor) VALUES ('pedido_teste', 0);
                CREATE TABLE devolucoes (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    sessao_id INTEGER NOT NULL REFERENCES sessoes(id),
                    pedido_id INTEGER NOT NULL REFERENCES pedidos(id),
                    caixa INTEGER NOT NULL,
                    forma INTEGER NOT NULL,
                    valor INTEGER NOT NULL,
                    motivo TEXT NOT NULL DEFAULT '',
                    criado_em TEXT NOT NULL
                );
                CREATE INDEX ix_devolucoes_sessao ON devolucoes (sessao_id);
                CREATE INDEX ix_devolucoes_pedido ON devolucoes (pedido_id);
                CREATE TABLE itens_devolucao (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    devolucao_id INTEGER NOT NULL REFERENCES devolucoes(id),
                    item_id INTEGER NOT NULL REFERENCES itens_pedido(id),
                    nome TEXT NOT NULL,
                    preco INTEGER NOT NULL,
                    quantidade INTEGER NOT NULL
                );
                CREATE INDEX ix_itens_devolucao ON itens_devolucao (devolucao_id);
                CREATE INDEX ix_itens_devolucao_item ON itens_devolucao (item_id);
                INSERT INTO versao (v) VALUES (2);
                """);
            t.Commit();
        }

        // Versão 3.3: combos (um preço só, fichas de outros produtos).
        if (versao < 3)
        {
            using var t = conexao.BeginTransaction();
            Executar(conexao, t, """
                CREATE TABLE componentes_combo (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    combo_id INTEGER NOT NULL REFERENCES produtos(id),
                    produto_id INTEGER NOT NULL REFERENCES produtos(id),
                    quantidade INTEGER NOT NULL,
                    valor INTEGER NOT NULL,
                    ordem INTEGER NOT NULL
                );
                CREATE INDEX ix_componentes_combo ON componentes_combo (combo_id);
                CREATE INDEX ix_componentes_combo_produto ON componentes_combo (produto_id);
                CREATE TABLE componentes_item (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    item_id INTEGER NOT NULL REFERENCES itens_pedido(id),
                    produto_id INTEGER,
                    nome TEXT NOT NULL,
                    detalhe TEXT NOT NULL DEFAULT '',
                    valor INTEGER NOT NULL,
                    quantidade INTEGER NOT NULL,
                    ordem INTEGER NOT NULL
                );
                CREATE INDEX ix_componentes_item ON componentes_item (item_id);
                ALTER TABLE itens_devolucao ADD COLUMN componente_id INTEGER;
                ALTER TABLE itens_devolucao ADD COLUMN valor INTEGER NOT NULL DEFAULT 0;
                UPDATE itens_devolucao SET valor = preco * quantidade;
                CREATE INDEX ix_itens_devolucao_componente ON itens_devolucao (componente_id);
                INSERT INTO versao (v) VALUES (3);
                """);
            t.Commit();
        }

        // Versão 3.4: as fichas do combo podem ser texto livre (produto, detalhe, quantidade e valor).
        if (versao < 4)
        {
            using var t = conexao.BeginTransaction();
            Executar(conexao, t, """
                CREATE TABLE componentes_combo_novo (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    combo_id INTEGER NOT NULL REFERENCES produtos(id),
                    produto_id INTEGER REFERENCES produtos(id),
                    nome TEXT NOT NULL,
                    detalhe TEXT NOT NULL DEFAULT '',
                    quantidade INTEGER NOT NULL,
                    valor INTEGER NOT NULL,
                    ordem INTEGER NOT NULL
                );
                INSERT INTO componentes_combo_novo (id, combo_id, produto_id, nome, detalhe, quantidade, valor, ordem)
                    SELECT c.id, c.combo_id, c.produto_id, COALESCE(p.nome, ''), COALESCE(p.detalhe, ''),
                           c.quantidade, c.valor, c.ordem
                    FROM componentes_combo c LEFT JOIN produtos p ON p.id = c.produto_id;
                DROP TABLE componentes_combo;
                ALTER TABLE componentes_combo_novo RENAME TO componentes_combo;
                CREATE INDEX ix_componentes_combo ON componentes_combo (combo_id);
                CREATE INDEX ix_componentes_combo_produto ON componentes_combo (produto_id);
                INSERT INTO versao (v) VALUES (4);
                """);
            t.Commit();
        }

        // Versão 3.7: guarda quanto cada venda tirou de verdade do estoque, para "Apagar as vendas" e o backup
        // devolverem exatamente isso (nem mais, se o estoque foi digitado de novo ou ligado depois da venda).
        if (versao < 5)
        {
            using var t = conexao.BeginTransaction();
            bool Adicionar(string tabela)
            {
                if (Escalar<long>(conexao, t, $"SELECT COUNT(*) FROM pragma_table_info('{tabela}') WHERE name = 'baixado'") > 0)
                    return false;
                Executar(conexao, t, $"ALTER TABLE {tabela} ADD COLUMN baixado INTEGER NOT NULL DEFAULT 0");
                return true;
            }
            // Vendas de antes da 3.7: como não se sabe quanto saiu, conta a venda inteira dos produtos que controlam
            // estoque (vendas pagas = status 1, fora do teste), como o estoque era baixado.
            if (Adicionar("itens_pedido"))
                Executar(conexao, t, """
                    UPDATE itens_pedido SET baixado = quantidade
                    WHERE produto_id IN (SELECT id FROM produtos WHERE controla_estoque = 1)
                      AND pedido_id IN (SELECT id FROM pedidos WHERE status = 1 AND teste = 0)
                    """);
            if (Adicionar("componentes_item"))
                Executar(conexao, t, """
                    UPDATE componentes_item
                    SET baixado = quantidade * (SELECT i.quantidade FROM itens_pedido i WHERE i.id = componentes_item.item_id)
                    WHERE produto_id IN (SELECT id FROM produtos WHERE controla_estoque = 1)
                      AND item_id IN (SELECT i.id FROM itens_pedido i JOIN pedidos p ON p.id = i.pedido_id
                                      WHERE p.status = 1 AND p.teste = 0)
                    """);
            Executar(conexao, t, "INSERT INTO versao (v) VALUES (5)");
            t.Commit();
        }

        // Versão 3.10: cada aba tem a sua grade (colunas × linhas) ou é automática (0), sem espaço vazio na tela.
        // As abas que já existem ficam com a grade que estava nas configurações.
        if (versao < 6)
        {
            using var t = conexao.BeginTransaction();
            if (Escalar<long>(conexao, t, "SELECT COUNT(*) FROM pragma_table_info('abas') WHERE name = 'colunas'") == 0)
            {
                var (colunas, linhas) = GradeDasConfiguracoes(conexao, t);
                Executar(conexao, t, $"""
                    ALTER TABLE abas ADD COLUMN colunas INTEGER NOT NULL DEFAULT 0;
                    ALTER TABLE abas ADD COLUMN linhas INTEGER NOT NULL DEFAULT 0;
                    UPDATE abas SET colunas = {colunas}, linhas = {linhas};
                    """);
            }
            Executar(conexao, t, "INSERT INTO versao (v) VALUES (6)");
            t.Commit();
        }

        // Ao abrir, o programa procura os pedidos que ficaram esperando a maquininha (status 0). Sem índice isso lia a
        // tabela de pedidos inteira (todas as festas guardadas). O índice só guarda esses poucos pedidos, então não
        // pesa na venda nem no disco. Conferido a cada abertura (não muda a versão do banco): vale também para um
        // banco copiado de outra máquina ou restaurado de uma cópia antiga.
        Executar(conexao, null, "CREATE INDEX IF NOT EXISTS ix_pedidos_pendentes ON pedidos (id) WHERE status = 0");
    }

    /// <summary>Colunas e linhas que estavam nas configurações (antes da 3.10 a grade era uma só para todas as abas).</summary>
    private static (int Colunas, int Linhas) GradeDasConfiguracoes(SqliteConnection conexao, SqliteTransaction t)
    {
        var json = Escalar<string>(conexao, t, "SELECT valor FROM config WHERE chave = 'geral'");
        int colunas = 4, linhas = 3;
        if (!string.IsNullOrEmpty(json))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("Colunas", out var c) && c.TryGetInt32(out var vc)) colunas = vc;
                if (doc.RootElement.TryGetProperty("Linhas", out var l) && l.TryGetInt32(out var vl)) linhas = vl;
            }
            catch (System.Text.Json.JsonException)
            {
                // configuração com defeito: fica a grade padrão
            }
        }
        return (Math.Clamp(colunas, 1, 6), Math.Clamp(linhas, 1, 8));
    }
}
