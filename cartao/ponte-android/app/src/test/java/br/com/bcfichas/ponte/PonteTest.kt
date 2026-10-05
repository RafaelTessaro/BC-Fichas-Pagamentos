package br.com.bcfichas.ponte

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.PipedInputStream
import java.io.PipedOutputStream
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executor
import java.util.concurrent.Executors
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger

/**
 * O app da maquininha sem maquininha: o PagBank é de mentira e o tablet manda as mesmas mensagens que o programa do
 * tablet manda. As regras são as do dinheiro: nunca cobrar duas vezes, guardar todo resultado, uma cobrança por vez.
 */
class PonteTest {
    @get:Rule
    val pasta = TemporaryFolder()

    /** PagBank de mentira: o teste decide quando e como cada cobrança termina. */
    private class PagadorFalso : Pagador {
        val cobrancas = AtomicInteger()
        val cancelamentos = AtomicInteger()
        val consultas = AtomicInteger()
        val decisoes = LinkedBlockingQueue<ResultadoPagamento>()
        var pronta = true
        @Volatile
        var ultima: ResultadoPagamento? = null
        var erroAoPagar = false
        /** Quantas vezes "a última aprovada" falha antes de responder (o serviço do PagBank ainda subindo). */
        @Volatile
        var falhasNaUltima = 0
        @Volatile
        private var pagando = false

        override fun info() = InfoTerminal("PagBank Moderninha Smart (P2)", "PB0123456", pronta,
            if (pronta) "" else "Ative a maquininha")

        override fun pagar(pedido: PedidoPagamento, avisar: (String) -> Unit): ResultadoPagamento {
            cobrancas.incrementAndGet()
            pagando = true
            try {
                avisar("Aproxime, insira ou passe o cartão na maquininha")
                if (erroAoPagar) throw IllegalStateException("serviço do PagBank caiu")
                return decisoes.poll(5, TimeUnit.SECONDS)?.copy(referencia = pedido.referencia)
                    ?: ResultadoPagamento(false, mensagem = "tempo esgotado")
            } finally {
                pagando = false
            }
        }

        /** Como o PagBank: cancelar só faz efeito com a tela de pagamento aberta. */
        override fun cancelar() {
            cancelamentos.incrementAndGet()
            if (pagando) decisoes.offer(ResultadoPagamento(false, cancelado = true, mensagem = "Operação cancelada"))
        }

        override fun ultimaAprovada(): ResultadoPagamento? {
            consultas.incrementAndGet()
            if (falhasNaUltima > 0) {
                falhasNaUltima--
                throw IllegalStateException("serviço do PagBank ainda não respondeu")
            }
            return ultima
        }
    }

    /** Uma ligação de mentira: guarda o que a maquininha mandou para o tablet. */
    private class Tablet : Ligacao {
        val recebidas = LinkedBlockingQueue<Mensagem>()
        var fechada = false
        override fun enviar(mensagem: Mensagem) {
            recebidas.offer(mensagem)
        }

        override fun fechar() {
            fechada = true
        }

        fun esperar(tipo: String): Mensagem {
            while (true) {
                val m = recebidas.poll(5, TimeUnit.SECONDS) ?: throw AssertionError("não chegou \"$tipo\"")
                if (m.tipo == tipo) return m
            }
        }
    }

    private val pagador = PagadorFalso()
    private val armazem by lazy { ArmazemArquivo(pasta.root.resolve("cobrancas.json")) }
    private val ponte by lazy { novaPonte() }

    private fun novaPonte(cobrancas: Executor = Executors.newSingleThreadExecutor(), guardar: Armazem = armazem) =
        Ponte(pagador, guardar, cobrancas, Executors.newCachedThreadPool(), tentativasRecuperar = 3, intervaloRecuperar = 10)

    private fun cobrar(id: String = "C01-P000005-12", valor: Long = 2350, forma: String = "credito",
                       referencia: String = "P0005AB12C") =
        """{"tipo":"cobrar","id":"$id","referencia":"$referencia","valor":$valor,"forma":"$forma","comprovante":false}"""

    private fun ligar(p: Ponte = ponte): Tablet {
        val t = Tablet()
        p.conectou(t)
        return t
    }

    @Test
    fun ola_conta_a_maquininha_e_se_esta_pronta() {
        val t = ligar()
        ponte.recebeu(t, """{"tipo":"ola","versao":1,"caixa":3,"programa":"BC Fichas Cartão"}""")
        val ola = t.esperar("ola")
        assertEquals(1, ola.campos.getInt("versao"))
        assertEquals("PB0123456", ola.texto("serial"))
        assertTrue(ola.campos.getBoolean("pronta"))
        assertEquals(3, ponte.situacao.caixa)
        ponte.recebeu(t, """{"tipo":"ping"}""")
        t.esperar("pong")
    }

    @Test
    fun cobranca_aprovada_volta_com_os_dados_e_fica_guardada() {
        val t = ligar()
        ponte.recebeu(t, cobrar())
        assertEquals("Aproxime, insira ou passe o cartão na maquininha", t.esperar("andamento").texto("texto"))
        pagador.decisoes.offer(ResultadoPagamento(true, mensagem = "Aprovado", autorizacao = "123456", nsu = "987",
            bandeira = "VISA", codigo = "ABC"))
        val r = t.esperar("resultado")
        assertTrue(r.campos.getBoolean("aprovado"))
        assertEquals("C01-P000005-12", r.id)
        assertEquals("987", r.texto("nsu"))
        assertEquals(2350L, r.valor)
        assertTrue(armazem.resultado("C01-P000005-12")!!.aprovado)
        assertEquals(2350L, armazem.resultado("C01-P000005-12")!!.valor)
        assertNull(armazem.emAndamento())

        // O mesmo pedido de novo: devolve o guardado, sem cobrar
        ponte.recebeu(t, cobrar())
        assertTrue(t.esperar("resultado").campos.getBoolean("aprovado"))
        assertEquals(1, pagador.cobrancas.get())

        // O mesmo identificador com outro valor não é esta cobrança: nem cobra, nem devolve o aprovado
        ponte.recebeu(t, cobrar(valor = 990))
        assertEquals("Este pedido foi cobrado com outro valor na maquininha.", t.esperar("erro").texto("mensagem"))
        ponte.recebeu(t, """{"tipo":"consultar","id":"C01-P000005-12","valor":990}""")
        t.esperar("erro")
        ponte.recebeu(t, """{"tipo":"consultar","id":"C01-P000005-12","valor":2350}""")
        assertTrue(t.esperar("resultado").campos.getBoolean("aprovado"))
        assertEquals(1, pagador.cobrancas.get())
    }

    @Test
    fun uma_cobranca_por_vez_e_consulta_conta_o_que_acontece() {
        val t = ligar()
        ponte.recebeu(t, cobrar())
        t.esperar("andamento")
        ponte.recebeu(t, cobrar(id = "C01-P000006-13"))
        assertEquals("C01-P000006-13", t.esperar("ocupada").id)
        ponte.recebeu(t, """{"tipo":"consultar","id":"C01-P000005-12"}""")
        assertEquals("Pagamento em andamento na maquininha", t.esperar("andamento").texto("texto"))
        ponte.recebeu(t, """{"tipo":"consultar","id":"C01-P999999-99"}""")
        t.esperar("desconhecida")
        pagador.decisoes.offer(ResultadoPagamento(false, mensagem = "Cartão recusado"))
        assertFalse(t.esperar("resultado").campos.getBoolean("aprovado"))
        assertEquals(1, pagador.cobrancas.get())
    }

    @Test
    fun ligacao_nova_toma_o_lugar_da_velha_e_recebe_o_resultado() {
        val velha = ligar()
        ponte.recebeu(velha, cobrar())
        velha.esperar("andamento")

        // O Bluetooth caiu e o tablet ligou de novo
        val nova = ligar()
        assertTrue(velha.fechada)
        ponte.recebeu(velha, """{"tipo":"consultar","id":"C01-P000005-12"}""") // a velha é ignorada
        ponte.recebeu(nova, """{"tipo":"consultar","id":"C01-P000005-12"}""")
        nova.esperar("andamento")
        pagador.decisoes.offer(ResultadoPagamento(true, mensagem = "Aprovado"))
        assertTrue(nova.esperar("resultado").campos.getBoolean("aprovado"))
        assertTrue(velha.recebidas.none { it.tipo == "resultado" })
    }

    @Test
    fun resultado_sem_ninguem_ligado_fica_guardado_para_a_consulta() {
        val t = ligar()
        ponte.recebeu(t, cobrar())
        t.esperar("andamento")
        ponte.desconectou(t)
        pagador.decisoes.offer(ResultadoPagamento(true, mensagem = "Aprovado"))
        while (armazem.resultado("C01-P000005-12") == null) Thread.sleep(10)
        val volta = ligar()
        ponte.recebeu(volta, """{"tipo":"consultar","id":"C01-P000005-12"}""")
        assertTrue(volta.esperar("resultado").campos.getBoolean("aprovado"))
    }

    @Test
    fun cancelar_pede_ao_pagbank_e_volta_cancelado() {
        val t = ligar()
        ponte.recebeu(t, cobrar(forma = "pix"))
        t.esperar("andamento")
        ponte.recebeu(t, """{"tipo":"cancelar","id":"outro"}""") // cancelar outra cobrança não faz nada
        ponte.recebeu(t, """{"tipo":"cancelar","id":"C01-P000005-12"}""")
        val r = t.esperar("resultado")
        assertTrue(r.campos.getBoolean("cancelado"))
        assertFalse(r.campos.getBoolean("aprovado"))
        assertEquals(1, pagador.cancelamentos.get())
    }

    @Test
    fun cancelar_que_chega_antes_de_o_pagbank_comecar_nao_se_perde() {
        // A cobrança fica na fila (o PagBank ainda não abriu a tela) quando o tablet pede para cancelar
        val fila = LinkedBlockingQueue<Runnable>()
        val p = novaPonte(cobrancas = Executor { fila.offer(it) })
        val t = ligar(p)
        p.recebeu(t, cobrar())
        p.recebeu(t, """{"tipo":"cancelar","id":"C01-P000005-12"}""")
        fila.take().run()
        val r = t.esperar("resultado")
        assertTrue(r.campos.getBoolean("cancelado"))
        assertEquals(0, pagador.cobrancas.get()) // nem abriu a tela de pagamento
    }

    @Test
    fun cobranca_invalida_nao_chega_no_pagbank() {
        val t = ligar()
        ponte.recebeu(t, """{"tipo":"cobrar","id":"X","valor":0,"forma":"credito"}""")
        t.esperar("erro")
        ponte.recebeu(t, """{"tipo":"cobrar","id":"X","valor":100,"forma":"dinheiro"}""")
        t.esperar("erro")
        ponte.recebeu(t, "isto não é json")
        assertEquals(0, pagador.cobrancas.get())
    }

    @Test
    fun erro_no_meio_so_vale_como_aprovado_se_o_pagbank_disser_e_se_nao_o_operador_confere() {
        val t = ligar()
        pagador.erroAoPagar = true
        ponte.recebeu(t, cobrar())
        // O PagBank não diz que foi esta: não dá para saber (o cliente pode ter pago)
        assertTrue(t.esperar("indefinida").texto("mensagem")!!.contains("Confira na tela da maquininha"))
        assertTrue(armazem.resultado("C01-P000005-12")!!.indefinido)
        // Pedir de novo não cobra de novo nem decide nada
        ponte.recebeu(t, cobrar())
        t.esperar("indefinida")
        assertEquals(1, pagador.cobrancas.get())

        // A última aprovada é esta (mesmo código e mesmo valor): vale
        pagador.ultima = ResultadoPagamento(true, mensagem = "Aprovado", referencia = "P0006XYZ12", valor = 100)
        ponte.recebeu(t, cobrar(id = "C01-P000006-13", valor = 100, referencia = "P0006XYZ12"))
        assertTrue(t.esperar("resultado").campos.getBoolean("aprovado"))

        // Mesmo código, outro valor: não é esta
        pagador.ultima = ResultadoPagamento(true, mensagem = "Aprovado", referencia = "P0007XYZ12", valor = 999)
        ponte.recebeu(t, cobrar(id = "C01-P000007-14", valor = 100, referencia = "P0007XYZ12"))
        t.esperar("indefinida")

        // O PagBank nem responde
        pagador.falhasNaUltima = 10
        ponte.recebeu(t, cobrar(id = "C01-P000008-15", valor = 100, referencia = "P0008XYZ12"))
        t.esperar("indefinida")
    }

    @Test
    fun app_que_fechou_no_meio_resolve_a_cobranca_ao_abrir() {
        armazem.marcarEmAndamento(PedidoPagamento("C01-P000007-20", "P0007AAAAA", 1000, "debito", false))
        pagador.ultima = ResultadoPagamento(true, mensagem = "Aprovado", referencia = "P0007AAAAA", nsu = "555", valor = 1000)
        // O serviço do PagBank demora a subir: pergunta de novo
        pagador.falhasNaUltima = 2
        val p = novaPonte()
        // Até resolver, o tablet não cobra outra coisa e ouve "andamento" sobre ela
        val t = ligar(p)
        p.recebeu(t, cobrar(id = "C01-P000009-30"))
        t.esperar("ocupada")
        p.recebeu(t, """{"tipo":"consultar","id":"C01-P000007-20"}""")
        t.esperar("andamento")
        p.recuperar()
        assertEquals(3, pagador.consultas.get())
        assertEquals("555", armazem.resultado("C01-P000007-20")!!.nsu)
        assertNull(armazem.emAndamento())
        assertTrue(t.esperar("resultado").campos.getBoolean("aprovado"))
    }

    @Test
    fun app_que_fechou_no_meio_sem_confirmacao_do_pagbank_deixa_para_o_operador() {
        // A última aprovada no PagBank é outra (de antes): esta pode ainda ser aprovada pelo PagBank, que termina o
        // pagamento mesmo com o app fechado. Não decide.
        armazem.marcarEmAndamento(PedidoPagamento("C01-P000008-21", "P0008BBBBB", 1000, "debito", false))
        pagador.ultima = ResultadoPagamento(true, mensagem = "Aprovado", referencia = "P0001CCCCC", valor = 1000)
        novaPonte().recuperar()
        assertTrue(armazem.resultado("C01-P000008-21")!!.indefinido)
        assertNull(armazem.emAndamento())

        // O PagBank não responde nunca: não vira "não aprovado", fica para o operador
        armazem.marcarEmAndamento(PedidoPagamento("C01-P000009-22", "P0009DDDDD", 1000, "debito", false))
        pagador.falhasNaUltima = 100
        pagador.consultas.set(0)
        novaPonte().recuperar()
        assertEquals(3, pagador.consultas.get())
        val r = armazem.resultado("C01-P000009-22")!!
        assertTrue(r.indefinido)
        assertFalse(r.aprovado)
        assertEquals(1000L, r.valor)
    }

    @Test
    fun sem_conseguir_gravar_nao_cobra() {
        val quebrado = object : Armazem by armazem {
            override fun marcarEmAndamento(pedido: PedidoPagamento?) {
                if (pedido != null) throw java.io.IOException("memória cheia")
                armazem.marcarEmAndamento(null)
            }
        }
        val p = novaPonte(guardar = quebrado)
        val t = ligar(p)
        p.recebeu(t, cobrar())
        assertTrue(t.esperar("erro").texto("mensagem")!!.contains("Nada foi cobrado"))
        assertEquals(0, pagador.cobrancas.get())
        assertNull(p.situacao.cobrando)
    }

    @Test
    fun armazem_guarda_no_arquivo_e_le_de_novo() {
        val arquivo = pasta.root.resolve("guardado.json")
        val a = ArmazemArquivo(arquivo)
        a.guardar("A", ResultadoPagamento(true, mensagem = "Aprovado", nsu = "1", valor = 2350))
        a.guardar("I", ResultadoPagamento(false, mensagem = "confira", valor = 100, indefinido = true))
        a.marcarEmAndamento(PedidoPagamento("B", "REFB", 500, "pix", true))
        val b = ArmazemArquivo(arquivo)
        assertEquals("1", b.resultado("A")!!.nsu)
        assertEquals(2350L, b.resultado("A")!!.valor)
        assertTrue(b.resultado("I")!!.indefinido)
        assertEquals(PedidoPagamento("B", "REFB", 500, "pix", true), b.emAndamento())
        repeat(ArmazemArquivo.MAXIMO + 10) { b.guardar("X$it", ResultadoPagamento(false, mensagem = "não")) }
        assertNull(b.resultado("A")) // só as últimas ficam
        assertEquals(ArmazemArquivo.MAXIMO, b.ultimas(1000).size)
    }

    @Test
    fun maquininha_que_desliga_no_meio_da_gravacao_fica_com_a_gravacao_anterior() {
        val arquivo = pasta.root.resolve("guardado.json")
        val a = ArmazemArquivo(arquivo)
        a.guardar("A", ResultadoPagamento(true, mensagem = "Aprovado", nsu = "1"))   // 1ª gravação: arquivo .a
        a.marcarEmAndamento(PedidoPagamento("B", "REFB", 500, "pix", true))         // 2ª: arquivo .b
        // A 2ª ficou pela metade (desligou no meio): vale a 1ª, inteira
        val b2 = pasta.root.resolve("guardado.json.b")
        b2.writeText(b2.readText().take(30))
        val b = ArmazemArquivo(arquivo)
        assertEquals("1", b.resultado("A")!!.nsu)
        assertNull(b.emAndamento())
        // E continua gravando por cima da estragada, sem perder a boa
        b.guardar("C", ResultadoPagamento(false, mensagem = "não"))
        val c = ArmazemArquivo(arquivo)
        assertEquals("1", c.resultado("A")!!.nsu)
        assertFalse(c.resultado("C")!!.aprovado)
    }

    @Test
    fun valor_do_pagbank_vira_centavos() {
        assertEquals(2350L, Mensagem.centavos("2350"))
        assertEquals(2350L, Mensagem.centavos("23,50"))
        assertEquals(123456L, Mensagem.centavos("R$ 1.234,56"))
        assertNull(Mensagem.centavos(null))
        assertNull(Mensagem.centavos("sem valor"))
    }

    @Test
    fun referencia_para_o_pagbank_tem_ate_10_letras_e_numeros() {
        assertEquals("C01P000005", Mensagem.referenciaLimpa("C01P000005", "x"))
        assertEquals("C01P000005", Mensagem.referenciaLimpa("C01-P000005 é", "x"))
        assertEquals("1P00000512", Mensagem.referenciaLimpa(null, "C01-P000005-12"))
    }

    @Test
    fun ligacao_por_fluxo_junta_os_pedacos_e_descarta_linha_gigante() {
        val doTablet = PipedOutputStream()
        val entrada = PipedInputStream(doTablet, 1 shl 20)
        val paraOTablet = PipedInputStream(1 shl 16)
        val saida = PipedOutputStream(paraOTablet)
        val fechou = CountDownLatch(1)
        val ligacao = LigacaoFluxo(entrada, saida, { fechou.countDown() }, ponte)
        ponte.conectou(ligacao)
        ligacao.iniciar()

        // Uma linha enorme (lixo) e um "ping" chegando em dois pedaços
        doTablet.write(("x".repeat(Mensagem.MAIOR_LINHA + 100) + "\n").toByteArray())
        doTablet.write("""{"tipo":"pi""".toByteArray())
        doTablet.flush()
        Thread.sleep(50)
        doTablet.write("""ng"}""".toByteArray() + "\n".toByteArray())
        doTablet.flush()
        val resposta = paraOTablet.bufferedReader().readLine()
        assertEquals("pong", Mensagem.ler(resposta)!!.tipo)

        doTablet.close()
        assertTrue(fechou.await(5, TimeUnit.SECONDS))
    }
}
