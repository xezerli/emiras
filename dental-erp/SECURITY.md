# Təhlükəsizlik siyasəti

## Zəifliyi necə bildirmək

Zəifliyi **açıq issue-da yazma**. GitHub-da repo üçün "Security → Report a vulnerability" (private advisory) istifadə et. Bildirişdə: təsir, təkrar addımları, təsirlənən versiya/commit. PHI və ya real pasiyent məlumatı **göndərmə**.

> Qeyd: reaksiya vaxtı (SLA) və rəsmi əlaqə ünvanı layihə sahibi tərəfindən təyin olunmalıdır; bu sənəd onları uydurmur.

## Əhatə

Bu repo pilot mərhələsindədir ([STATUS](docs/STATUS.md)). Bilinən boşluqlar [security-and-privacy §9](docs/guides/security-and-privacy.md#9-bilinən-boşluqlar-təhlükəsizlik-baxımından-ciddilik-sırası-ilə)-da açıq yazılıb; onları bildirmək lazım deyil, amma düzəliş təklifi gözəldir.

## Avtomatik yoxlamalar

Hər PR: `pnpm audit --prod`, NuGet zəiflik yoxlaması, Trivy, CodeQL. Dependabot yeniləmələri `.github/dependabot.yml`-dədir.
