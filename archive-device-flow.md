### Archive Device Flow (Mermaid)

```mermaid
graph TB
    %% 1) Intake & Validation
    subgraph "1. Request Intake"
        A[Client Submits Archive Device Request] --> B[Archive Device Validation]
        B --> C[Extract Archive Device Model]
        C --> D{JasperCustomerId Present?}
    end

    %% 2) Customer Mapping / Auth
    subgraph "2. Customer Resolution & Auth"
        D -- No --> E[Resolve Customer Mapping]
        E --> F{Customer Mapping Found?}
        F -- No --> X1[Fail: Unknown Customer]
        F -- Yes --> G[Setup Jasper API Authentication]
        D -- Yes --> G
    end

    %% 3) Service Provider Resolution
    subgraph "3. Service Provider Resolution"
        G --> H[Resolve Target Service Provider (Teal)]
        H --> I{Service Provider Exists?}
        I -- No --> X2[Fail: Unknown Service Provider]
        I -- Yes --> J[Confirm Archive Eligibility Rules]
        J --> K{Eligible to Archive?}
        K -- No --> X3[Fail: Archive Not Allowed]
    end

    %% 4) Device Preconditions
    subgraph "4. Device Preconditions"
        K -- Yes --> L{Devices Provided?}
        L -- No --> X4[Fail: No Devices Provided]
        L -- Yes --> M[Process Each Device]
    end

    %% 5) Device Processing Loop
    subgraph "5. Archive Processing Loop"
        M --> N[Submit Jasper API: Archive Device]
        N --> O[Save Device Archive Result]
        O --> P{Success?}
        P -- No --> Q[Log Device Error & Continue]
        P -- Yes --> R{More Devices?}
        Q --> R
        R -- Yes --> M
        R -- No --> S[Log Operation Result]
    end

    %% 6) Post Processing & Completion
    subgraph "6. Post Processing"
        S --> T[Update AMOP/Jasper Link Tables]
        T --> U[Mark Archive Complete]
        U --> V[Complete]
    end

    %% Error paths converge to operation logging
    X1 --> S
    X2 --> S
    X3 --> S
    X4 --> S

    %% Styling
    style A fill:#e1f5fe
    style N fill:#fff3e0
    style V fill:#e8f5e8
    style X1 fill:#ffebee
    style X2 fill:#ffebee
    style X3 fill:#ffebee
    style X4 fill:#ffebee
```

