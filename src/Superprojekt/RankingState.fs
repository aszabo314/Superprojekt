namespace Superprojekt

open FSharp.Data.Adaptive

module RankingState =
    let datasetHidden = cset<string> HashSet.empty
