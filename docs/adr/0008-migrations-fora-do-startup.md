# ADR-0008 — Migrations fora do start da aplicação

**Status:** Aceita · **Data:** 2026-09-12

## Contexto

`Database.Migrate()` no `Program.cs` é o padrão mais comum em projetos .NET — e funciona
bem com uma instância. O enunciado exige que a aplicação permaneça correta **entre 2 e 11
réplicas**.

Com N pods subindo em paralelo após um `helm upgrade`, todos executam o mesmo start: N
tentativas concorrentes de aplicar a mesma migration. O PostgreSQL evita a corrupção com
lock, mas os resultados vão de timeout a pods em `CrashLoopBackOff` durante o deploy — e
o pior caso é uma migration longa segurando lock enquanto a versão anterior já saiu de
serviço.

## Decisão

Migrations **nunca** são aplicadas pelo processo da API.

| Ambiente | Mecanismo |
|---|---|
| Local | `dotnet ef database update` ou o serviço `migrator` do Compose |
| Docker Compose | Serviço `migrator` com a mesma imagem, `restart: "no"`; a `api` declara `depends_on: { migrator: { condition: service_completed_successfully } }` |
| Kubernetes | `Job` com hooks `pre-install`/`pre-upgrade` do Helm, `backoffLimit: 2` |

O Compose reproduz de propósito o modelo de produção — um passo separado que termina
antes de a API subir — para que a diferença entre ambientes não esconda o problema.

Regra complementar: **toda migration precisa ser compatível com a versão anterior da
aplicação**, porque durante o *rolling update* pods antigos e novos convivem por alguns
segundos. Mudanças destrutivas (remover ou renomear coluna em uso, adicionar `NOT NULL`
sem default) seguem expandir → migrar dados → contrair, em *releases* separados.

## Consequências

**Positivas**

- Nenhuma corrida entre réplicas na subida, com qualquer número de pods.
- Falha de migration é visível e bloqueante: o hook `pre-upgrade` impede o `Deployment`
  de avançar, e a versão anterior continua servindo — em vez de pods novos subirem contra
  schema errado.
- O estado do schema fica desacoplado do ciclo de vida dos pods; um pod reiniciado por
  OOM não tenta migrar nada.

**Negativas**

- Um passo a mais no pipeline de deploy, e um serviço a mais no Compose: `docker compose
  up` deixa de ser "sobe a API e pronto".
- Exige disciplina de compatibilidade retroativa nas migrations — que é necessária de
  qualquer forma para *rolling update* sem downtime.
- Desenvolvedor que roda `dotnet run` direto precisa lembrar de aplicar as migrations.
  Mitigado: a aplicação **verifica** se há migration pendente no start e falha com
  mensagem explícita dizendo o comando a executar (verificar é barato e seguro; aplicar é
  que não é).

## Alternativas descartadas

| Alternativa | Por que não |
|---|---|
| `Database.Migrate()` no start | A corrida que motiva esta ADR |
| Migrar só se `replicas == 1` | Condicional frágil que depende de configuração de deploy; falha justamente quando escala |
| Lock consultivo (`pg_advisory_lock`) no start | Resolve a corrida, mas mantém migration longa no caminho de subida e pode estourar o `initialDelaySeconds` do probe, causando reinício |
| `initContainer` por pod | N execuções concorrentes — mesma corrida, embrulhada diferente |
| Migration manual fora do deploy | Passo humano esquecível, e schema divergente entre ambientes |

Ver [operations.md](../operations.md#migrations).
