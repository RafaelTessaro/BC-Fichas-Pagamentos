package br.com.bcfichas.ponte

import java.io.ByteArrayOutputStream
import java.io.Closeable
import java.io.InputStream
import java.io.OutputStream
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit
import kotlin.concurrent.thread

/**
 * Uma ligação com o tablet sobre um fluxo de bytes (o socket Bluetooth). Uma linha (thread) lê as mensagens e
 * entrega para a [Ponte]; outra escreve as respostas, na ordem, sem segurar quem respondeu.
 */
class LigacaoFluxo(
    private val entrada: InputStream,
    private val saida: OutputStream,
    private val socket: Closeable,
    private val ponte: Ponte,
) : Ligacao {
    private val fila = LinkedBlockingQueue<String>()

    @Volatile
    private var aberta = true

    fun iniciar() {
        thread(name = "ponte-leitura", isDaemon = true) { ler() }
        thread(name = "ponte-escrita", isDaemon = true) { escrever() }
    }

    override fun enviar(mensagem: Mensagem) {
        if (aberta) fila.offer(mensagem.linha())
    }

    override fun fechar() {
        aberta = false
        try {
            socket.close()
        } catch (_: Exception) {
            // Já estava fechada
        }
    }

    private fun ler() {
        val buffer = ByteArray(4096)
        val linha = ByteArrayOutputStream()
        var descartando = false
        try {
            while (aberta) {
                val lidos = entrada.read(buffer)
                if (lidos < 0) break
                for (i in 0 until lidos) {
                    val b = buffer[i]
                    if (b != '\n'.code.toByte()) {
                        if (linha.size() < Mensagem.MAIOR_LINHA) linha.write(b.toInt()) else descartando = true
                        continue
                    }
                    if (!descartando) ponte.recebeu(this, linha.toString("UTF-8").trimEnd('\r'))
                    linha.reset()
                    descartando = false
                }
            }
        } catch (_: Exception) {
            // Ligação caiu
        } finally {
            fechar()
            ponte.desconectou(this)
        }
    }

    private fun escrever() {
        try {
            while (aberta) {
                val linha = fila.poll(500, TimeUnit.MILLISECONDS) ?: continue
                saida.write(linha.toByteArray(Charsets.UTF_8))
                saida.flush()
            }
        } catch (_: Exception) {
            fechar()
        }
    }
}
