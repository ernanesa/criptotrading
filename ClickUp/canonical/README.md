# Snapshot da documentação canônica

Esta pasta contém uma cópia local dos arquivos de `plan/` existentes no momento
da exportação. Ela facilita consulta offline e revisão do backlog sem depender
do ClickUp. A fonte normativa continua sendo `../plan/` até uma eventual
decisão explícita de mover a autoridade do projeto.

Os arquivos foram copiados sem alteração semântica; para conferir divergências:

```bash
diff -ru plan ClickUp/canonical
```

Não trate este snapshot como evidência de implementação. Código, testes e
resultados observados continuam obrigatórios para mover um cartão a `Done`.
