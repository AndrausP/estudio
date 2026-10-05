# Estúdio

Aplicativo de estudo para **Windows** que usa IA para montar a sua trilha: diagnóstico inicial, módulos em sequência, aulas com exemplos, exercícios com feedback, provas, cartões de revisão espaçada, tutor e cronômetro de foco. Tudo fica salvo no seu computador.

> Projeto independente. Não é afiliado à Anthropic, à OpenAI ou a qualquer provedor de IA. Leia os [Termos de uso](TERMOS.md).

## Recursos

- Diagnóstico obrigatório para começar no nível certo.
- Trilha de módulos editável; nota mínima de 70% para liberar o próximo.
- Aulas, prática (texto ou código), provas com nova versão a cada tentativa.
- Revisão espaçada com cartões gerados após cada aula.
- Tutor IA, busca (Ctrl+F), foco/Pomodoro, mapa de constância e backups diários.
- **Material de estudo:** ao criar a trilha (ou depois, em ⚙ Projeto) anexe PDFs, Word, Markdown, texto, código e imagens; a IA usa o conteúdo no diagnóstico, na trilha e nas aulas.
- **Detalhes por módulo:** diga o que quer em cada módulo (foco, tipo de exemplo, o que evitar), se quer exemplos e se quer a lousa.
- **Lousa:** quadro para desenhar à mão e pedir diagramas à IA; as aulas trazem cenas da lousa com exemplos. Salva imagem em `Documentos\Estudio`.
- **Sobre você:** texto livre que a IA resume e passa a usar para entender seu nível e seu jeito de aprender.
- **Escolha do modelo de IA:** em Conexões de IA, escolha ou digite o modelo (a CLI oferece opus/sonnet/haiku e versões; APIs listam os modelos disponíveis).
- **Excluir trilha:** botão **✕** ao lado de cada trilha na barra lateral (ou em ⚙ Projeto), com confirmação.

## Instalação (usuário)

### Requisitos

- Windows 10/11 64 bits.
- Uma conexão de IA, **uma das duas**:
  - **Claude Code CLI** (conta própria): instale o `claude` e faça login com `claude auth login`. Veja a documentação oficial do Claude Code.
  - **API compatível com OpenAI**: URL base (ex.: `https://api.openai.com/v1`), modelo e chave.

> O Estúdio não inclui nem fornece acesso a nenhum serviço de IA. Planos e APIs de terceiros podem ser cobrados pelo provedor.

### Passo a passo

1. Abra a página de **Releases** deste repositório e baixe `Estudio-Setup.exe`.
2. Execute o instalador. Se o Windows SmartScreen avisar (o instalador não é assinado), clique em **Mais informações → Executar assim mesmo**.
3. Leia os termos e marque **Li e aceito**.
4. Escolha a **pasta de instalação** (ou use a sugerida) e se quer os atalhos na área de trabalho e no menu Iniciar.
5. Clique em **Instalar** e depois em **Concluir**.
6. No Estúdio, abra **Conexões de IA**, configure a Claude Code CLI ou a API e use **Testar conexão**.
7. Crie um projeto, faça o diagnóstico, revise a trilha e confirme para começar.

### Desinstalar

Execute `Desinstalar.cmd` na pasta de instalação. Remove o programa e os atalhos. Seus dados ficam em `%LOCALAPPDATA%\Estudio` e só são apagados se você apagar a pasta.

### Onde ficam os dados

- Projetos, progresso e cartões: `%LOCALAPPDATA%\Estudio`.
- Backups diários (7 cópias): `%LOCALAPPDATA%\Estudio\backups`.
- Arquivos anexados: `%LOCALAPPDATA%\Estudio\attachments`; perfil "Sobre você": `%LOCALAPPDATA%\Estudio\profile.json`.

> O instalador não leva nenhum dado de usuário: o app abre limpo em cada computador.
- Cadernos exportados: `Documentos\Estudio`.

## Compilar a partir do código-fonte

Requisitos: [.NET SDK 8+](https://dotnet.microsoft.com/download) no Windows e PowerShell.

```powershell
git clone https://github.com/AndrausP/estudio.git
cd estudio

# testes
dotnet test tests/StudyDesk.Tests

# rodar sem instalar
dotnet run --project src/StudyDesk.Desktop

# gerar o instalador em dist\Estudio-Setup.exe
powershell -ExecutionPolicy Bypass -File build.ps1
```

Estrutura:

| Pasta | Conteúdo |
|-------|----------|
| `src/StudyDesk.Domain` | Entidades e regras |
| `src/StudyDesk.Application` | Contratos e utilitários |
| `src/StudyDesk.Infrastructure` | IA, banco local (SQLite), exportação, busca |
| `src/StudyDesk.Desktop` | Interface (Avalonia), lousa e telas |
| `tests/` | Testes NUnit |
| `installer/` | Instalador (WinForms) |

## Limites

- A qualidade de aulas, respostas e correções depende da IA escolhida; confira os conteúdos.
- Sem sincronização entre computadores, sem notificações fora do app e sem modelos locais.
- O texto digitado e o conteúdo do projeto são enviados ao provedor de IA configurado. Não envie dados sensíveis.
