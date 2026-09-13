# ADR-0004 — Exemplares como contagem, não como entidades

**Status:** Aceita · **Data:** 2026-09-12

## Contexto

O enunciado diz que "uma biblioteca possui livros e vários exemplares de cada livro" e,
nos requisitos funcionais, pede identificar cada livro por "título, ISBN, autor e
**quantidade de exemplares**". Nenhuma operação pedida menciona exemplar individual:
emprestar é "realizar empréstimo de um exemplar disponível", não "do exemplar nº 3".

Duas modelagens possíveis:

- `books.total_copies` / `books.available_copies` — contadores na linha do livro.
- Tabela `copies` com uma linha por exemplar físico, cada uma com estado.

## Decisão

**Contagem.** `Book` guarda `TotalCopies` e `AvailableCopies`; `Loan` referencia o livro,
não um exemplar.

## Consequências

**Positivas**

- O empréstimo vira o decremento condicional de um contador — uma instrução atômica que
  resolve a concorrência sem lock explícito ([ADR-0003](0003-concorrencia-update-condicional.md)).
- A invariante de estoque cabe em um `CHECK` constraint.
- Menos tabelas, menos joins, menos código, para exatamente a mesma funcionalidade
  requisitada.

**Negativas**

- **Não é possível rastrear qual exemplar físico está com quem.** Uma biblioteca real
  precisa disso para código de tombo, estado de conservação e extravio.
- Toda a concorrência do sistema se concentra em uma linha por título. Com exemplares
  individuais, a disputa se distribuiria entre linhas diferentes — o que só importaria em
  volume muito acima do escopo.
- `AvailableCopies` é um dado derivado mantido de forma incremental. Se algum caminho
  futuro errar a conta, ele diverge da contagem real de empréstimos ativos. Mitigações:
  o `CHECK`, o fato de a atualização nunca ser *read-modify-write*, e
  `availableCopiesAfter` gravado em cada evento de auditoria, que permite reconstruir a
  série e detectar divergência depois do fato.

## Migração futura

Se exemplares individuais passarem a ser necessários, o caminho é: criar `copies`,
popular `total_copies` linhas por livro, e trocar o decremento condicional por
`UPDATE copies SET status='OnLoan' WHERE book_id=@b AND status='Available' AND id=(SELECT id … LIMIT 1 FOR UPDATE SKIP LOCKED)`.
`SKIP LOCKED` daria, inclusive, mais paralelismo do que a solução atual. A API externa
mudaria pouco: `POST /loans` continuaria recebendo `bookId`, e o `loanId` passaria a
carregar também o `copyId`.

## Alternativas descartadas

| Alternativa | Por que não |
|---|---|
| Tabela `copies` desde já | Dobra o modelo e o código para atender a requisitos que não existem no enunciado |
| `available_copies` calculado por `COUNT` de empréstimos ativos a cada leitura | Custo por leitura na consulta mais chamada do sistema, e reintroduziria a janela ler-decidir-escrever na hora de emprestar |
| Coluna gerada / *materialized view* | Não pode participar da condição atômica do `UPDATE` que resolve a concorrência |

Ver [domain-model.md](../domain-model.md) e as
[limitações conhecidas](../../README.md#9-limitações-conhecidas).
