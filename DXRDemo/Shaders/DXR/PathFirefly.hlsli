/*
 * Copyright (c) 2025, NVIDIA CORPORATION. All rights reserved.
 * NVIDIA CORPORATION and its licensors retain all intellectual property and
 * proprietary rights in and to this software, related documentation and any
 * modifications thereto. Any use, reproduction, disclosure or distribution
 * without an express license agreement from NVIDIA CORPORATION is prohibited.
 * Adapted from RTXPT PathTracerHelpers.hlsli; see Licenses/RTXPT.txt.
 */
float NextFireflyK(float current,float pdf,float lobeProbability) {
    float angle=pdf>0?2*acos(clamp(1-1/(2*PI*pdf),-1,1)):0;
    return max(1e-5,current*(32/(32+angle*angle))*sqrt(saturate(lobeProbability)));
}
float FireflyWeight(float3 radiance,float k) {
    if(PathSettings.z<=0) return 1; // Reference transport is never clipped.
    float average=dot(radiance,1.0/3.0);
    return min(1,PathSettings.z*k/max(average,1e-20));
}
