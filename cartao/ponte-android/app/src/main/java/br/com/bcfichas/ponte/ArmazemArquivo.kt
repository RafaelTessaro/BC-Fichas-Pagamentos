package br.com.bcfichas.ponte

import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.FileOutputStream

/**
 * Os resultados das cobranças num arquivo da memória do app (não usa cartão de memória). Guarda as últimas
 * [MAXIMO]: o tablet só pergunta das cobranças recentes.
 */
class ArmazemArquivo(private val arquivo: File) : Armazem {
    private val trava = Any()
    private val resultados = LinkedHashMap<String, ResultadoPagamento>()
    private var emAndamento: PedidoPagamento? = null

    init {
        ler()
    }

    override fun resultado(id: String): ResultadoPagamento? = synchronized(trava) { resultados[id] }

    override fun guardar(id: String, resultado: ResultadoPagamento) = synchronized(trava) {
        resultados.remove(id)
        resultados[id] = resultado
        while (resultados.size > MAXIMO) resultados.remove(resultados.keys.first())
        gravar()
    }

    override fun emAndamento(): PedidoPagamento? = synchronized(trava) { emAndamento }

    override fun marcarEmAndamento(pedido: PedidoPagamento?) = synchronized(trava) {
        emAndamento = pedido
        gravar()
    }

    override fun ultimas(quantas: Int): List<Pair<String, ResultadoPagamento>> =
        synchronized(trava) { resultados.entries.reversed().take(quantas).map { it.key to it.value } }

    /** Grava num arquivo novo e troca: se a maquininha desligar no meio, fica o arquivo antigo inteiro. */
    private fun gravar() {
        val json = JSONObject()
        val lista = JSONArray()
        for ((id, r) in resultados) lista.put(paraJson(r).put("id", id))
        json.put("resultados", lista)
        emAndamento?.let {
            json.put(
                "emAndamento", JSONObject().put("id", it.id).put("referencia", it.referencia).put("valor", it.valor)
                    .put("forma", it.forma).put("comprovante", it.comprovante)
            )
        }
        val novo = File(arquivo.path + ".novo")
        FileOutputStream(novo).use { saida ->
            saida.write(json.toString().toByteArray(Charsets.UTF_8))
            saida.fd.sync()
        }
        if (!novo.renameTo(arquivo)) {
            arquivo.delete()
            novo.renameTo(arquivo)
        }
    }

    private fun ler() {
        val origem = if (arquivo.exists()) arquivo else File(arquivo.path + ".novo")
        if (!origem.exists()) return
        try {
            val json = JSONObject(origem.readText(Charsets.UTF_8))
            val lista = json.optJSONArray("resultados") ?: JSONArray()
            for (i in 0 until lista.length()) {
                val r = lista.getJSONObject(i)
                resultados[r.getString("id")] = deJson(r)
            }
            json.optJSONObject("emAndamento")?.let {
                emAndamento = PedidoPagamento(
                    it.getString("id"), it.optString("referencia"), it.optLong("valor"), it.optString("forma"),
                    it.optBoolean("comprovante"),
                )
            }
        } catch (e: Exception) {
            // Arquivo estragado: começa vazio (o tablet confere o que ficou sem resposta)
        }
    }

    companion object {
        const val MAXIMO = 500

        private fun paraJson(r: ResultadoPagamento) = JSONObject()
            .put("aprovado", r.aprovado).put("cancelado", r.cancelado).put("mensagem", r.mensagem)
            .putOpt("autorizacao", r.autorizacao).putOpt("nsu", r.nsu).putOpt("bandeira", r.bandeira)
            .putOpt("codigo", r.codigo).putOpt("referencia", r.referencia)

        private fun deJson(j: JSONObject) = ResultadoPagamento(
            aprovado = j.optBoolean("aprovado"), cancelado = j.optBoolean("cancelado"), mensagem = j.optString("mensagem"),
            autorizacao = j.opcional("autorizacao"), nsu = j.opcional("nsu"), bandeira = j.opcional("bandeira"),
            codigo = j.opcional("codigo"), referencia = j.opcional("referencia"),
        )

        private fun JSONObject.opcional(chave: String): String? = if (has(chave) && !isNull(chave)) optString(chave) else null
    }
}
