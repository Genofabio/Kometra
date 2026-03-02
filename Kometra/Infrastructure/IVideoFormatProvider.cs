using System.Collections.Generic;
using System.Threading.Tasks;
using Kometra.Models.Visualization;
using Kometra.Models.Export;

namespace Kometra.Infrastructure;

public interface IVideoFormatProvider
{
    Task InitializeAsync();
    IEnumerable<VideoContainer> GetSupportedContainers();
    IEnumerable<VideoCodec> GetSupportedCodecs(VideoContainer container);
    string GetExtension(VideoContainer container);
    int GetFourCC(VideoCodec codec, VideoContainer container);
    
    // NUOVO: Restituisce l'API che ha funzionato per quel container/codec
    OpenCvSharp.VideoCaptureAPIs GetBestAPI(VideoContainer container, VideoCodec codec);
}