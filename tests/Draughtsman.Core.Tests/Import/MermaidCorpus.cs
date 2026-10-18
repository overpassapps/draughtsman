using Draughtsman.Core.Enums;

namespace Draughtsman.Core.Tests.Import;

/// <summary>One real-world Mermaid snippet with what the importer must produce from it.</summary>
public sealed record MermaidCase(
    string Name,
    DocumentType Type,
    string Source,
    int Nodes,
    int Edges,
    IReadOnlyDictionary<string, string> Kinds,
    string? Direction = "TB")
{
    public override string ToString()
    {
        return Name;
    }
}

/// <summary>
/// Snippets in the shapes LLMs and humans actually paste: README diagrams, decision flows, deployment sketches,
/// with quotes, unicode, entities, multi-line labels and the terser forms Mermaid tolerates.
/// </summary>
public static class MermaidCorpus
{
    public static IEnumerable<object[]> Cases()
    {
        return All.Select(c => new object[] { c });
    }

    public static readonly IReadOnlyList<MermaidCase> All =
    [
        new("basic chain",
            DocumentType.Flowchart,
            """
            flowchart TD
                A[Christmas] -->|Get money| B(Go shopping)
                B --> C{Let me think}
                C -->|One| D[Laptop]
                C -->|Two| E[iPhone]
                C -->|Three| F[fa:fa-car Car]
            """,
            Nodes: 6, Edges: 5,
            new Dictionary<string, string> { ["n_A"] = "process", ["n_B"] = "process", ["n_C"] = "decision" }),

        new("all shapes",
            DocumentType.Flowchart,
            """
            flowchart LR
                s([Start]) --> p[Process] --> r(Rounded) --> sub[[Subroutine]]
                sub --> db[(Database)] --> c((Circle)) --> asym>Note] --> d{Decision}
                d --> h{{Hexagon}} --> par[/Input/] --> alt[\Output\] --> dbl(((Stop)))
            """,
            Nodes: 12, Edges: 11,
            new Dictionary<string, string>
            {
                ["n_s"] = "terminal", ["n_p"] = "process", ["n_r"] = "process", ["n_sub"] = "subprocess", ["n_db"] = "stored-data",
                ["n_c"] = "terminal", ["n_asym"] = "note", ["n_d"] = "decision", ["n_h"] = "process", ["n_par"] = "io",
                ["n_alt"] = "io", ["n_dbl"] = "terminal",
            },
            Direction: "LR"),

        new("architecture with subgraphs and database",
            DocumentType.Architecture,
            """
            graph LR
                user([Customer]) --> web[Web app]
                subgraph vpc [Production VPC]
                    web --> api[Orders API]
                    api --> db[(Postgres)]
                    api -.-> cache[(Redis)]
                    api --> mq{{Kafka}}
                end
                api --> stripe[[Stripe]]
            """,
            Nodes: 8, Edges: 6,
            new Dictionary<string, string>
            {
                ["n_user"] = "actor", ["n_web"] = "service", ["n_api"] = "service", ["n_db"] = "database",
                ["n_cache"] = "database", ["n_mq"] = "queue", ["n_stripe"] = "external", ["c_vpc"] = "container",
            },
            Direction: "LR"),

        new("quoted labels with brackets and unicode",
            DocumentType.Flowchart,
            """
            flowchart TD
                id1["This is the (text) in the box"]
                id2["Straße → Café ☕"]
                id3["A [nested] bracket"] -->|"pipe|label"| id1
                id1 --> id2
            """,
            Nodes: 3, Edges: 2,
            new Dictionary<string, string> { ["n_id1"] = "process" }),

        new("entities and line breaks",
            DocumentType.Flowchart,
            """
            flowchart LR
                A["A double quote:#quot;"] --> B["A dec char:#9829;"]
                B --> C[Line one<br/>Line two<br>Line three]
            """,
            Nodes: 3, Edges: 2,
            new Dictionary<string, string> { ["n_C"] = "process" },
            Direction: "LR"),

        new("edge text forms and ampersand fan-out",
            DocumentType.Flowchart,
            """
            flowchart TB
                A -- text --> B -- more text --> C
                D -. dotted text .-> E
                F == thick text ==> G
                H --- I
                J -.- K
                L === M
                N & O --> P & Q
            """,
            Nodes: 17, Edges: 11,
            new Dictionary<string, string> { ["n_A"] = "process" }),

        new("dotted links without spaces",
            DocumentType.Flowchart,
            """
            flowchart LR
                A-.->B
                B-.-C
                C-. text .->D
                D-.->E-.-F
            """,
            Nodes: 6, Edges: 5,
            new Dictionary<string, string> { ["n_A"] = "process", ["n_F"] = "process" },
            Direction: "LR"),

        new("one-line header with semicolons and a trailing comment",
            DocumentType.Flowchart,
            """
            graph LR; A-->B; B-->C %% laid out left to right
                C-->D %% trailing comment
            """,
            Nodes: 4, Edges: 3,
            new Dictionary<string, string> { ["n_D"] = "process" },
            Direction: "LR"),

        new("comments, semicolons and directives",
            DocumentType.Flowchart,
            """
            %%{init: {'theme': 'forest'}}%%
            graph TD;
                %% This is a comment
                A-->B; B-->C;
                C-->D
                %% trailing comment
            """,
            Nodes: 4, Edges: 3,
            new Dictionary<string, string> { ["n_D"] = "process" }),

        new("nested subgraphs with direction and titles",
            DocumentType.Flowchart,
            """
            flowchart TB
                c1-->a2
                subgraph one
                a1-->a2
                end
                subgraph two
                b1-->b2
                end
                subgraph three
                direction LR
                c1-->c2
                subgraph inner ["Inner group"]
                    c3
                end
                end
                one --> two
                three --> two
                two --> c2
            """,
            Nodes: 11, Edges: 7,
            new Dictionary<string, string> { ["c_one"] = "container", ["c_inner"] = "container", ["n_c3"] = "process" }),

        new("CI pipeline with styling that is skipped",
            DocumentType.Flowchart,
            """
            flowchart LR
                classDef green fill:#9f6,stroke:#333;
                push([git push]) --> lint[Lint] & test[Test]
                lint --> build[Build image]:::green
                test --> build
                build --> deploy{Main branch?}
                deploy -- yes --> prod[(Prod DB migrate)]
                deploy -- no --> done([Done])
                prod --> done
                class push,done green
                style deploy fill:#f96
                click build "https://example.com/build" _blank
                linkStyle 0 stroke:#ff3,stroke-width:4px;
            """,
            Nodes: 7, Edges: 8,
            new Dictionary<string, string> { ["n_push"] = "terminal", ["n_deploy"] = "decision", ["n_prod"] = "stored-data", ["n_done"] = "terminal" },
            Direction: "LR"),

        new("fenced block with front matter title",
            DocumentType.Flowchart,
            """
            ```mermaid
            ---
            title: Order fulfilment
            ---
            flowchart TD
                start([Order received]) --> stock{In stock?}
                stock -->|Yes| pick[Pick & pack]
                stock -->|No| backorder[/Back-order notice/]
                pick --> ship([Shipped])
                backorder --> ship
            ```
            """,
            Nodes: 5, Edges: 5,
            new Dictionary<string, string> { ["n_start"] = "terminal", ["n_stock"] = "decision", ["n_backorder"] = "io" }),

        new("bidirectional, circle and cross arrows",
            DocumentType.Architecture,
            """
            flowchart LR
                A <--> B
                B --o C
                C --x D
                D <-.-> E
                E o--o F
            """,
            Nodes: 6, Edges: 5,
            new Dictionary<string, string> { ["n_A"] = "service" },
            Direction: "LR"),

        new("no header and hyphenated ids",
            DocumentType.Flowchart,
            """
            order-service --> payment-gateway
            payment-gateway-->ledger.v2
            """,
            Nodes: 3, Edges: 2,
            new Dictionary<string, string> { ["n_order-service"] = "process", ["n_ledger.v2"] = "process" }),

        new("multi-line labels in quotes and long chains",
            DocumentType.Architecture,
            """
            graph TB
                lb["Load balancer
                (nginx)"] --> app1[App 1] & app2[App 2]
                app1 & app2 --> db[(Primary)] --> replica[(Replica)]
                app1 -->|"publish
                events"| bus{{Event bus}}
            """,
            Nodes: 6, Edges: 6,
            new Dictionary<string, string> { ["n_lb"] = "service", ["n_db"] = "database", ["n_replica"] = "database", ["n_bus"] = "queue" }),
    ];
}
