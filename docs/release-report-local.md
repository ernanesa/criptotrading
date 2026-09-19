# Relatório pós-release local

`PostReleaseReportGenerator` implementa o contrato local de `AC-RELEASE-REPORT-01`.
Ele produz um artefato JSON canônico e redigido para uma janela já encerrada,
sem fazer chamadas à exchange, sem carregar credenciais e sem conceder
autoridade de promoção.

O relatório registra:

- janela, ambiente, modo, decisão, owner, reviewer e approver;
- versões das fontes que produziram a evidência;
- contadores de confiabilidade, risco, execução, estratégia, incidentes,
  intervenções e divergências;
- follow-ups;
- hash SHA-256 do JSON canônico.

O gerador falha fechado quando a evidência está incompleta ou inconsistente:
timestamps precisam ser UTC, a janela precisa ter duração positiva, a decisão
precisa ser uma das opções registradas, as fontes não podem estar vazias e os
contadores não podem ser negativos ou contraditórios. Dados sensíveis e
identificadores de ordens não fazem parte do contrato.

Exemplo de uso em código:

```csharp
var artifact = PostReleaseReportGenerator.Generate(input);
File.WriteAllText("release-evidence.json", artifact.CanonicalJson);
```

O exemplo de escrita é apenas ilustrativo. O arquivo final deve receber as
permissões locais da política de evidências (`0600`) e ser anexado ao manifesto
de rastreabilidade. Um relatório `Go` não substitui a aprovação humana nem os
gates de Testnet, OOS, Shadow, alertas e RPO/RTO de produção.

## Bundle de evidência e rastreabilidade

Para persistir o relatório de forma integrada ao fluxo local, use
`PostReleaseEvidenceWriter.Write`. Ele cria dois arquivos imutáveis no diretório
informado:

- `<report-id>.json`: o JSON canônico redigido do relatório;
- `<report-id>.manifest.json`: manifesto sidecar com o SHA-256 do relatório,
  versão do schema, decisão, referências de rastreabilidade e
  `promotion_authorized: false`.

```csharp
var bundle = PostReleaseEvidenceWriter.Write(
    "artifacts/release",
    input,
    [
        "AC-RELEASE-REPORT-01",
        "docs/readiness/traceability-20260919.yaml",
        "<commit-audited>",
    ],
    generatedAtUtc: clock.GetUtcNow());
```

O escritor ordena e valida as referências, rejeita IDs de arquivo inseguros e
metadados com aparência de credencial, exige timestamp UTC e aplica `0600` em
Unix. Uma segunda gravação com o mesmo conteúdo é idempotente; qualquer
tentativa de alterar uma evidência existente falha fechado. A existência do
manifesto não aprova uma promoção e não substitui o `validate-readiness.sh` nem
as aprovações humanas.

Os testes do contrato estão em
`tests/CryptoTrading.Shadow.Tests/PostReleaseReportTests.cs`.
