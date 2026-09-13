# ADR-0001 — CQRS dentro de vertical slices, em um projeto

**Status:** Aceita · **Data:** 2026-09-12

## Contexto

O desafio pede uma API pequena (13 endpoints) cujas decisões difíceis se concentram em um
caso de uso: criar um empréstimo sob concorrência. O enunciado diz explicitamente que
"a adoção de Clean Architecture, DDD, CQRS ou qualquer outro padrão não será avaliada
isoladamente; as decisões precisam resolver problemas concretos do desafio".

Duas forças em tensão:

- Camadas genéricas (Domain/Application/Infrastructure/Api em quatro projetos) dariam
  isolamento, ao custo de dezenas de arquivos de indireção — interfaces de repositório,
  mapeadores, DTOs por camada — para um domínio com três entidades.
- Um controlador gordo com `DbContext` injetado seria direto demais: transação, cache,
  auditoria e idempotência acabariam repetidos e fora de ordem em cada método.

## Decisão

**Um projeto de API, organizado por feature (vertical slices), com CQRS explícito dentro
de cada slice.**

- `Features/{Catalog,Users,Loans,Audit,Auth}/`, cada um com `Domain/`, `Commands/`,
  `Queries/`, `Contracts/` e seus endpoints.
- Escrita e leitura são tipos diferentes, resolvidos por handlers diferentes, com
  pipelines diferentes.
- O que é transversal fica em `Infrastructure/`.

A separação comando/query não é cosmética: os dois caminhos têm exigências opostas neste
sistema — transação obrigatória vs. nenhuma, rastreamento do EF vs. `AsNoTracking`,
banco como origem vs. cache como origem, invalidação vs. população, auditoria vs. nada.
Cada preocupação vira um decorator aplicado só a quem precisa, e a **ordem** entre elas
fica escrita em um lugar só, em vez de repetida e reinventada por método.

## Consequências

**Positivas**

- Cada caso de uso cabe em um arquivo: comando, validator e handler juntos. Ler
  "como se cria um empréstimo" não exige atravessar quatro projetos.
- Transação, idempotência, invalidação de cache e métricas ficam no pipeline, na ordem
  certa, sem repetição — e a ordem é testável.
- Queries não carregam o peso do caminho de escrita (sem transação, sem tracking).

**Negativas**

- Não há fronteira compilada impedindo um slice de referenciar o interno de outro. É
  compensada por um teste de arquitetura, não pelo compilador.
- Uma equipe habituada a Clean Architecture precisa de um momento de adaptação para
  encontrar as coisas — mitigado pelo mapa em [architecture.md](../architecture.md).

## Alternativas descartadas

| Alternativa | Por que não |
|---|---|
| Clean Architecture em 4 projetos | Custo alto de indireção para três entidades. O isolamento que ela garante por compilação é obtido aqui por convenção + teste de arquitetura, com uma fração dos arquivos |
| Serviços por entidade (`BookService`, `LoanService`) | Cada método teria de decidir em runtime se abre transação, se invalida cache, se grava auditoria — exatamente a repetição que o pipeline elimina |
| CQRS com bancos/modelos de leitura separados | Traria consistência eventual e projeções para um sistema cujo requisito central é *não* ter janelas de inconsistência. O cache já cobre a necessidade de leitura rápida, com TTL explícito |
| Event sourcing | A trilha de auditoria exigida é de negócio, não de estado. `audit_events` resolve o requisito sem reconstruir estado a partir de eventos |
