# Kometra

![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20macOS%20%7C%20Linux-lightgrey)
![Framework](https://img.shields.io/badge/Framework-.NET%208.0-512BD4)
![UI](https://img.shields.io/badge/UI-Avalonia-purple)
![License](https://img.shields.io/badge/License-MIT-green)

**Kometra** è un sistema software integrato, multipiattaforma e ad alte prestazioni, concepito esclusivamente per l'elaborazione astrometrica e l'analisi morfologica avanzata di immagini cometarie.

Il progetto nasce come lavoro di Tesi Magistrale in Ingegneria Informatica presso il **Politecnico di Torino**, in stretta collaborazione con i ricercatori dell'**Osservatorio Astrofisico di Asiago**, con l'obiettivo di fornire alla comunità scientifica e astrofila uno strumento moderno per superare i limiti dei flussi di lavoro tradizionali.

---

## Funzionalità Principali

Kometra offre una pipeline completa per il trattamento del dato astronomico, dall'importazione del file grezzo fino all'esportazione dell'analisi morfologica:

- **Infrastruttura Dati FITS Proprietaria:** Parser FITS scritto da zero, con supporto a Multi-Extension FITS (MEF) e Tiled Image Compression (algoritmi Rice e GZIP bit-exact).
- **Astrometria e Tracking Dinamico:** Rilevamento sub-pixel dell'optocentro cometario per allineamento dinamico. Integrazione con risolutori astrometrici locali (ASTAP) e database effemeridali (JPL/NASA).
- **FITS Header Editor & Diagnostica:** Interfaccia interattiva per l'ispezione non distruttiva dei metadati, con valutazione semaforica dei requisiti astrometrici (*Health Evaluator*).
- **Starless Pipeline (Segmentazione e Inpainting):** Rimozione automatica delle stelle di campo tramite algoritmi di morfologia matematica e ricostruzione stocastica del fondo cielo, preservando l'integrità fotometrica della chioma.
- **Filtri Morfologici Avanzati:** Implementazione rigorosa di filtri rotazionali, radiali e tensoriali (es. Larson-Sekanina, Median Coma Model, Hessiana di Frangi) per l'estrazione di getti di polvere e strutture a basso contrasto.
- **Flusso di Lavoro a Nodi (Node Tree):** Paradigma non distruttivo che permette di condurre esperimenti paralleli (A/B testing) e visualizzare anteprime in tempo reale senza alterare i file originali.
- **Media & Scientific Export:** Esportazione di dati scientifici in FITS/MEF (compressi e non) e generazione automatizzata di timelapse video per la divulgazione.

## Stack Tecnologico

Il software è stato ingegnerizzato per massimizzare le prestazioni e garantire la portabilità:

- **Core & Logica:** C# / .NET
- **Interfaccia Grafica (GUI):** Avalonia UI (Pattern MVVM)
- **Motore Matematico:** OpenCV (tramite wrapper .NET) per le manipolazioni matriciali ad alta precisione.
- **Gestione Memoria:** Ottimizzazioni per matrici massive in virgola mobile tramite multithreading concorrenziale, `ArrayPool` e `ThreadLocal` per prevenire i colli di bottiglia del Garbage Collector.

## Installazione e Compilazione

Essendo basato su .NET e Avalonia, Kometra può essere compilato ed eseguito nativamente su **Windows, macOS e Linux**.

### Prerequisiti
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download) (o superiore)

### Build da sorgente
Clona il repository e compila il progetto:
```bash
git clone [https://github.com/tuo-username/Kometra.git](https://github.com/tuo-username/Kometra.git)
cd Kometra
dotnet build -c Release
```

## Screenshots

### Workspace & Node Tree
<img src="https://github.com/user-attachments/assets/1b6c7589-25ae-4aeb-99f2-da73c623432c" width="65%" alt="Workspace & Node Tree">

<br>

### FITS Header Editor
<img src="https://github.com/user-attachments/assets/32838a5b-10e7-4c8e-a66d-8e785ab1cfc6" width="65%" alt="FITS Header Editor">

<br>

### Filtri Morfologici
<img src="https://github.com/user-attachments/assets/d1ead85f-4be0-472c-9704-cecc50f1e8e5" width="65%" alt="Filtri Morfologici">

<br>

### Esportazione Timelapse
<img src="https://github.com/user-attachments/assets/fa931aec-253e-4382-8132-b9893d63e012" width="65%" alt="Esportazione Timelapse">
