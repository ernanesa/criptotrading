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

Os testes do contrato estão em
`tests/CryptoTrading.Shadow.Tests/PostReleaseReportTests.cs`.
