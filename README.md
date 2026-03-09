# Kometra

![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20macOS%20%7C%20Linux-lightgrey)
![Framework](https://img.shields.io/badge/Framework-.NET%208.0-512BD4)
![UI](https://img.shields.io/badge/UI-Avalonia-purple)
![License](https://img.shields.io/badge/License-GPLv3-blue)

**Kometra** is an integrated, cross-platform, and high-performance software system specifically designed for the astrometric processing and advanced morphological analysis of cometary images. 

🌍 *The application interface is fully available in both **English** and **Italian**.*

The project originated as a Master's Thesis in Computer Engineering at **Politecnico di Torino**, in close collaboration with researchers from the **Asiago Astrophysical Observatory**, with the aim of providing the scientific and amateur astronomy community with a modern tool to overcome the limitations of traditional workflows.

---

## Main Features

Kometra offers a comprehensive pipeline for astronomical data processing, from importing raw files to exporting morphological analysis:

- **Proprietary FITS Data Infrastructure:** Custom-built FITS parser supporting Multi-Extension FITS (MEF) and Tiled Image Compression (Rice and bit-exact GZIP algorithms).
- **Astrometry and Dynamic Tracking:** Sub-pixel detection of the cometary optocenter for dynamic alignment. Integration with local astrometric solvers (ASTAP) and ephemeris databases (JPL/NASA).
- **FITS Header Editor & Diagnostics:** Interactive interface for non-destructive metadata inspection, featuring a traffic-light evaluation system for astrometric requirements (*Health Evaluator*).
- **Starless Pipeline (Segmentation and Inpainting):** Automatic removal of field stars using mathematical morphology algorithms and stochastic background sky reconstruction, preserving the photometric integrity of the coma.
- **Advanced Morphological Filters:** Rigorous implementation of rotational, radial, and tensorial filters (e.g., Larson-Sekanina, Median Coma Model, Frangi Hessian) for the extraction of dust jets and low-contrast structures.
- **Node Tree Workflow:** Non-destructive paradigm that allows conducting parallel experiments (A/B testing) and real-time previews without altering the original files.
- **Media & Scientific Export:** Export of scientific data in FITS/MEF (compressed or uncompressed) and automated generation of video timelapses for outreach.

## Tech Stack

The software has been engineered to maximize performance and ensure portability:

- **Core & Logic:** C# / .NET
- **Graphical User Interface (GUI):** Avalonia UI (MVVM Pattern)
- **Mathematical Engine:** OpenCV (via .NET wrapper) for high-precision matrix manipulations.
- **Memory Management:** Optimizations for massive floating-point matrices through concurrent multithreading, `ArrayPool`, and `ThreadLocal` to prevent Garbage Collector bottlenecks.

## Installation and Build

Being based on .NET and Avalonia, Kometra can be compiled and run natively on **Windows, macOS, and Linux**.

### Prerequisites
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download) (or higher)

### Build from source
Clone the repository and build the project:
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

### Morphological Filters
<img src="https://github.com/user-attachments/assets/d1ead85f-4be0-472c-9704-cecc50f1e8e5" width="65%" alt="Morphological Filters">

<br>

### Timelapse Export
<img src="https://github.com/user-attachments/assets/fa931aec-253e-4382-8132-b9893d63e012" width="65%" alt="Timelapse Export">

---

## License

This project is licensed under the **GNU General Public License v3.0** (GPLv3) - see the [LICENSE](LICENSE) file for details.
