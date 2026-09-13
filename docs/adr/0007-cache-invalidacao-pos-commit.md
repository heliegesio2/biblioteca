# ADR-0007 — Invalidação explícita de cache após o commit

**Status:** Aceita · **Data:** 2026-09-12

## Contexto

O enunciado exige Redis em pelo menos uma consulta de leitura, com invalidação ou
atualização coerente quando empréstimo, devolução ou alteração de quantidade afetar o
dado — mantendo o PostgreSQL como fonte de verdade para a **decisão** de disponibilidade.

A consulta óbvia para cachear é `GET /books/{id}/availability`: a mais chamada e a que
muda com mais frequência. Ou seja, o melhor candidato a cache é também o mais perigoso.

## Decisão

Três regras, nesta ordem de importância:

1. **O cache nunca decide.** O caminho de escrita não lê o cache em momento algum. A
   disponibilidade que decide um empréstimo é a do `UPDATE` condicional no PostgreSQL
   ([ADR-0003](0003-concorrencia-update-condicional.md)).
2. **Invalidação explícita, sempre depois do commit.** Handlers enfileiram as chaves
   afetadas em um `ICacheInvalidationQueue` *scoped*; o `CacheInvalidationDecorator`,
   que roda por **fora** do `TransactionDecorator`, drena a fila após o commit.
3. **Remover, não atualizar.** A entrada é deletada; o próximo leitor busca no banco.

TTL curto como rede de segurança: 30s para disponibilidade, 300s para o livro.

## Por que "depois do commit" é a parte que importa

Invalidar antes do commit é um bug clássico e silencioso:

```
T1: DELETE cache:book:X         ← invalidou cedo demais
T2:                             GET availability → miss → lê o banco (valor ANTIGO,
                                T1 ainda não commitou) → grava no cache
T1: COMMIT                      ← banco tem o valor novo, cache tem o antigo
                                  e sobrevive até o TTL
```

Invalidando depois do commit, a janela se reduz ao tempo de uma chamada ao Redis.

E "remover em vez de atualizar" evita a corrida simétrica: dois comandos concorrentes
escrevendo seus valores no Redis em ordem arbitrária podem deixar o **mais velho**
vencendo. Remover é sempre seguro.

## Consequências

**Positivas**

- Perder o Redis inteiro degrada latência, nunca corretude. Leituras caem para o banco;
  escritas nem tocam no cache.
- A coerência não depende de o autor de cada handler lembrar de invalidar na hora certa:
  a ordem está no pipeline, em um lugar só.
- Falha de invalidação é **contada** (`biblioteca.cache.invalidation.failed`) e limitada
  pelo TTL, em vez de silenciosa e indefinida.

**Negativas**

- O L1 do `HybridCache` vive na memória de cada pod: a invalidação remove do Redis e do
  L1 **daquele** pod, e os demais servem a cópia local até ela expirar. Por isso
  `LocalCacheExpiration` é curto e explícito (5s) — janela conhecida em vez de
  indefinida. Um *backplane* pub/sub resolveria, ao custo de mais mecanismo.
- Invalidar em vez de atualizar significa um miss garantido logo após cada escrita.
  Aceitável: escrita é rara comparada à leitura.
- A `GET /books` (listagem) fica sem cache — decisão consciente, por ter espaço de chaves
  ilimitado e invalidação imprecisa.

## Alternativas descartadas

| Alternativa | Por que não |
|---|---|
| Escrever o valor novo no cache (*write-through*) | Corrida entre comandos concorrentes; o valor mais velho pode vencer e persistir até o TTL |
| Só TTL, sem invalidação | O enunciado exige coerência após empréstimo/devolução; 30s de disponibilidade errada é visível ao usuário |
| Invalidar dentro da transação | Repopulação com valor antigo antes do commit (diagrama acima) |
| Cache como fonte para decidir o empréstimo | Violaria o requisito explícito e reintroduziria a janela que [ADR-0003](0003-concorrencia-update-condicional.md) elimina |
| Cachear todo `GET` | Consome memória e serve dado velho onde a taxa de acerto é baixa |

Detalhes, chaves e testes: [caching.md](../caching.md).
