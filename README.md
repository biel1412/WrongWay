# Nexus

![Nexus](src/Nexus.Web/wwwroot/img/nexus-logo.png)

Inspeção de acesso da cooperativa **3069**.

Aplicação web em **C# (Blazor WebAssembly / .NET 10)** que cruza a planilha de atividades (diária ou mensal)
com a planilha de usuários e mostra, num dashboard, se **usuários não autorizados estão atuando** no volume das
cooperativas monitoradas.

Ela roda inteira no navegador e fica hospedada de graça no **GitHub Pages**. As planilhas **não são enviadas
para nenhum servidor**: o arquivo é lido no próprio computador de quem abre a página.

---

## Publicar no GitHub (uma vez só)

1. Crie um repositório no GitHub (ex.: `coop-audit`) e envie esta pasta para ele (pelo site: *Add file → Upload files*, arrastando tudo, inclusive a pasta `.github`).
2. No repositório, vá em **Settings → Pages → Build and deployment → Source** e escolha **GitHub Actions**.
3. Abra a aba **Actions** e espere o fluxo *Publicar no GitHub Pages* ficar verde (5 a 10 min na primeira vez).
4. O site fica em `https://SEU-USUARIO.github.io/coop-audit/`.

A cada alteração enviada para a branch `main`, o site é atualizado sozinho.

> Repositório privado: o GitHub Pages em repositório privado exige plano pago (Pro/Team/Enterprise).
> Mesmo com o site público, ninguém vê dados: cada pessoa só enxerga as planilhas que ela mesma abrir.

## Usar

1. **Planilha de atividades**: o mesmo layout do compilado (`IDPROCESSO`, `NOMEPROCESSO`, …, `USUARIO`, `ATIVIDADE`,
   `PROCEDIMENTO`, `DATAHORAINICIOATIVIDADE`, `DATAHORAFIMATIVIDADE`, `JUSTIFICATIVA`).
   Pode ser de um dia ou do mês inteiro. Também dá para selecionar vários arquivos diários de uma vez: linhas repetidas são descartadas.
2. **Lista de autorizados**: uma aba chamada `3069_autorizados`, com um login por linha na coluna A
   (a primeira linha pode ser o título `lista`). O modelo oficial está na tela inicial
   (`wwwroot/modelos/usuarios_modelo.xlsx`). Abas de outras cooperativas são ignoradas.
3. Clique em **Analisar planilhas**.

### Como a classificação funciona

Só a cooperativa **3069** é verificada. Modelo de **lista de autorizados**: só quem está na aba `3069_autorizados` pode atuar nela.

| Situação          | Regra                                                         |
|-------------------|---------------------------------------------------------------|
| ✅ Autorizado      | o usuário está na aba `3069_autorizados`                      |
| ⛔ Não autorizado  | qualquer outro usuário que atuou na coop 3069 → **irregular**  |

Para auditar outra cooperativa, troque o código em `src/Nexus.Web/Services/AuditScope.cs`.

- **Qual coop é a da atividade?** No painel você escolhe: `INSTITUICAOORIGEM`, `INSTITUICADESTINO` ou qualquer uma das duas (padrão).
- Maiúsculas/minúsculas, acentos e zeros à esquerda são ignorados (`0300` = `300`, `laisa.caetano` = `LAISA.CAETANO`).

### O que o painel mostra

- Alerta com o total de atividades irregulares e quem as fez
- Indicadores: volume do arquivo, volume nas coops monitoradas, atividades e usuários irregulares, volume feito por autorizados
- Um card por cooperativa com a divisão do volume e os usuários sinalizados
- Volume por hora (arquivo diário) ou por dia (mensal), separado por situação
- Ranking de usuários por volume e os procedimentos feitos nas atividades irregulares
- Tabelas com busca e exportação em CSV (abre direto no Excel): ocorrências irregulares, usuários por coop e lista de autorizados
- Filtros por cooperativa, período e critério de coop

## Rodar no computador (opcional)

Requer o [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet run --project src/Nexus.Web
```

## Estrutura

```
src/
  Nexus.Core/          regras de negócio (sem dependências externas)
    Xlsx/XlsxWorkbook.cs   leitor .xlsx em streaming (rápido e leve no navegador)
    UserDirectory.cs       leitura da lista de autorizados por coop
    ActivityLoader.cs      leitura da planilha de atividades (por nome de coluna)
    AuditEngine.cs         cruzamento e indicadores do dashboard
  Nexus.Web/           interface Blazor WebAssembly
    Pages/Home.razor       tela de envio das planilhas
    Components/            dashboard, gráficos, tabelas
    wwwroot/css/app.css    tema visual (pinho + latão)
    wwwroot/img/           logo (símbolo SVG e versão para apresentação)
    wwwroot/fonts/         Schibsted Grotesk e IBM Plex, hospedadas no próprio site (OFL)
.github/workflows/deploy.yml   publicação automática no GitHub Pages
```

Para aceitar outro nome de coluna, inclua-o em `HeaderMap` no `ActivityLoader.cs`.
