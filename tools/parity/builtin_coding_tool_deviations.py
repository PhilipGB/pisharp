"""Explicit safety exclusions retain failed strict comparisons and all observations."""


READ_IMAGE_BOUNDARIES = {
    'read-image-input-safety-boundary': {
        'hint': '17x11, displayed at 17x11. Multiply coordinates by 1.00',
        'width': 17, 'height': 11,
        'rgbaSha256': 'c41d826336119783a942d4b989a6a8081995c7b4c1ac48b702e58393a7238787'},
    'read-image-pixel-safety-boundary': {
        'hint': '8001x4000, displayed at 2000x1000. Multiply coordinates by 4.00',
        'width': 2000, 'height': 1000,
        'rgbaSha256': 'f11cf69490d6847fba73631e3404edf2a0d849a0fb4635127a639e36dcd9a2af'}
}


def assess_read_safety_deviation(case, observations):
    policy = READ_IMAGE_BOUNDARIES.get(case['caseId'])
    if not policy or case.get('acceptanceClass') != 'intentional-safety-boundary':
        return False
    expected = observations['pi']
    actual = observations['pisharp']
    note = 'Read image file [image/png]\n[Image: original ' + policy['hint'] + ' to map to original image.]'
    pixels = {key: policy[key] for key in ['width', 'height', 'rgbaSha256']}
    shared = {'caseId': case['caseId'], 'details': None, 'isError': False, 'rejected': False,
              'updateContract': {'initial': None, 'final': None}}
    oracle = {**shared, 'content': [{'type': 'text', 'text': note},
              {'type': 'image', 'mimeType': 'image/png', 'data': pixels}],
              'structuredContent': {'type': 'image', 'mimeType': 'image/png', 'data': pixels, 'note': note}}
    omitted = 'Read image file [image/png]\n[Image omitted: could not be resized below the inline image size limit.]'
    native = {**shared, 'content': [{'type': 'text', 'text': omitted}], 'structuredContent': omitted}
    return (expected['result'] == oracle and actual['result'] == native and
            expected['filesystem'] == actual['filesystem'] and
            expected['fullOutputArtifacts'] == actual['fullOutputArtifacts'] == [])
