"""Explicit native JPEG envelope; strict pixel observations remain in every report."""
import base64
import io
import math
import hashlib

def assess_jpeg_codec(reference_result, native_result, policy):
    from PIL import Image
    reference_blocks = [item for item in reference_result['content'] if item['type'] == 'image']
    native_blocks = [item for item in native_result['content'] if item['type'] == 'image']
    if len(reference_blocks) != 1 or len(native_blocks) != 1:
        return None
    reference, native = reference_blocks[0], native_blocks[0]
    if reference['data'] != policy['referenceImageBase64']:
        return None
    if reference['mimeType'] != native['mimeType']:
        return None
    for result, block in [(reference_result, reference), (native_result, native)]:
        structured = result.get('structuredContent')
        if not isinstance(structured, dict) or structured.get('data') != block['data']:
            return None
    with Image.open(io.BytesIO(base64.b64decode(reference['data']))) as image:
        reference_size, reference_pixels = image.size, image.convert('RGBA').tobytes()
        reference_quantization = getattr(image, 'quantization', None)
        reference_sampling = getattr(image, 'layer', None)
    with Image.open(io.BytesIO(base64.b64decode(native['data']))) as image:
        native_size, native_pixels = image.size, image.convert('RGBA').tobytes()
        native_quantization = getattr(image, 'quantization', None)
        native_sampling = getattr(image, 'layer', None)
    if reference_size != native_size or reference_pixels[3::4] != native_pixels[3::4]:
        return None
    delta = [abs(a - b) for index, (a, b) in enumerate(zip(reference_pixels, native_pixels)) if index % 4 != 3]
    stats = {'maximumChannelDelta': max(delta), 'meanAbsoluteDelta': sum(delta) / len(delta),
             'pairedMse': sum(value * value for value in delta) / len(delta), 'kind': policy['kind']}
    if policy['kind'] == 'jpeg_decoder':
        accepted = (stats['maximumChannelDelta'] <= policy['maximumChannelDelta'] and
                    stats['meanAbsoluteDelta'] <= policy['maximumMeanAbsoluteDelta'])
    elif policy['kind'] == 'jpeg_encoder':
        if (reference['mimeType'] != 'image/jpeg' or reference_quantization != native_quantization or
                reference_sampling != native_sampling or
                any(layer[1:3] != (1, 1) for layer in native_sampling)):
            return None
        original = base64.b64decode(policy['referenceRgbaBase64'])
        if (len(original) != len(reference_pixels) or
                hashlib.sha256(original).hexdigest() != policy['referenceRgbaSha256']):
            return None

        def mse(pixels):
            return sum((pixels[index] - original[index]) ** 2 for index in range(len(original))
                       if index % 4 != 3) / (len(original) // 4 * 3)
        reference_mse, native_mse = mse(reference_pixels), mse(native_pixels)
        stats.update(referenceMse=reference_mse, nativeMse=native_mse,
                     quantizationMatched=True, samplingMatched=True)
        accepted = (native_mse <= reference_mse * policy['maximumNativeDistortionRatio'] and
                    stats['pairedMse'] <= reference_mse * policy['maximumPairedDistortionRatio'])
    else:
        return None
    if not all(math.isfinite(value) for value in stats.values() if isinstance(value, (int, float))):
        return None
    return {**stats, 'accepted': accepted}


def only_codec_pixel_differences(paths):
    allowed = {'/result/content/1/data/rgbaSha256', '/result/structuredContent/data/rgbaSha256'}
    return bool(paths) and all(path in allowed for path in paths)
