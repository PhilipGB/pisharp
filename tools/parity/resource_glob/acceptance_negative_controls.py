#!/usr/bin/env python3
"""Executable rejection controls for the milestone acceptance verifier."""
import json
from pathlib import Path
import tempfile
import unittest

import acceptance
import create_manifest


class AcceptanceNegativeControls(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)

    def tearDown(self):
        self.temp.cleanup()

    def write(self, name, value):
        path = self.root / name
        path.write_text(json.dumps(value, indent=2) + '\n')
        return path

    def reports(self):
        reports=[]
        for case in json.loads(acceptance.PROCESS_INDEX.read_text()):
            extensions = case.get('expectedExtensions', {})
            extension_selection = dict(selectedCommands=extensions.get('selectedCommands', []),
                                       excludedCommandPresent=False, loadTrace=[],
                                       sourceInfoByCommand=extensions.get('sourceInfoByCommand', {}))
            result = dict(settingsSelection=case['expectedPi'], localExtensionSelection=extension_selection)
            reports.append(dict(scenario=case['id'], piSha=acceptance.PI_SHA,
                piSharpBaseSha='source', match=True,
                results={'pi':result,'pisharp':json.loads(json.dumps(result))}))
        return reports

    def write_reports(self, reports):
        return [self.write(f'report-{index}.json', value) for index,value in enumerate(reports)]

    def complete_manifest(self):
        bundle = self.root / 'evidence-bundle'
        for relative in acceptance.expected_report_inventory():
            path = bundle / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text('{}\n')
        return bundle, create_manifest.create_manifest(bundle, 'source', '42', 'head')

    def test_rejects_wrong_pi_and_pisharp_sha(self):
        reports=self.reports(); reports[0]['piSha']='wrong'
        with self.assertRaisesRegex(ValueError,'wrong Pi SHA'):
            acceptance.verify_process_cases(acceptance.PROCESS_INDEX,self.write_reports(reports),'source')
        reports=self.reports(); reports[0]['piSharpBaseSha']='stale'
        with self.assertRaisesRegex(ValueError,'stale PiSharp source'):
            acceptance.verify_process_cases(acceptance.PROCESS_INDEX,self.write_reports(reports),'source')

    def test_rejects_missing_duplicate_or_unexecuted_process_cases(self):
        with self.assertRaisesRegex(ValueError,'missing, duplicate, unexecuted'):
            acceptance.verify_process_cases(acceptance.PROCESS_INDEX,[],'source')
        paths=self.write_reports(self.reports())
        with self.assertRaisesRegex(ValueError,'duplicate'):
            acceptance.verify_process_cases(acceptance.PROCESS_INDEX,[*paths,paths[0]],'source')

    def test_rejects_failed_process_and_wrong_frozen_selection(self):
        reports=self.reports(); reports[0]['match']=False
        with self.assertRaisesRegex(ValueError,'process differential failed'):
            acceptance.verify_process_cases(acceptance.PROCESS_INDEX,self.write_reports(reports),'source')
        reports=self.reports(); reports[0]['results']['pisharp']['settingsSelection']={}
        with self.assertRaisesRegex(ValueError,'PiSharp selection differs'):
            acceptance.verify_process_cases(acceptance.PROCESS_INDEX,self.write_reports(reports),'source')

    def test_rejects_wrong_extension_selection_order(self):
        report=next(item for item in self.reports() if item['scenario']=='settings-extensions-combined')
        case=next(item for item in json.loads(acceptance.PROCESS_INDEX.read_text())
                  if item['id']==report['scenario'])
        report['results']['pi']['settingsSelection']=case['expectedPi']
        report['results']['pisharp']['settingsSelection']=case['expectedPi']
        report['results']['pi']['localExtensionSelection']=dict(
            selectedCommands=case['expectedExtensions']['selectedCommands'], excludedCommandPresent=False,
            sourceInfoByCommand=case['expectedExtensions']['sourceInfoByCommand'])
        report['results']['pisharp']['localExtensionSelection']=dict(
            selectedCommands=list(reversed(case['expectedExtensions']['selectedCommands'])),
            excludedCommandPresent=False,sourceInfoByCommand=case['expectedExtensions']['sourceInfoByCommand'])
        reports=self.reports(); reports=[report if item['scenario']==report['scenario'] else item for item in reports]
        with self.assertRaisesRegex(ValueError,'extension selection/order differs'):
            acceptance.verify_process_cases(acceptance.PROCESS_INDEX,self.write_reports(reports),'source')

    def test_rejects_missing_or_changed_evidence(self):
        bundle, value = self.complete_manifest()
        value['files'].append(dict(path='missing.json',sha256='0'))
        value['requiredFiles'] = len(value['files'])
        manifest=bundle/'manifest.json'; manifest.write_text(json.dumps(value,indent=2)+'\n')
        with self.assertRaisesRegex(ValueError,'missing evidence artifact'):
            acceptance.verify_evidence_manifest(manifest,'source','42','head')
        bundle, value = self.complete_manifest()
        artifact=bundle/'capture.json'; artifact.write_text('{}')
        value['files'].append(dict(path='capture.json',sha256='0'))
        value['requiredFiles'] = len(value['files'])
        manifest=bundle/'manifest.json'; manifest.write_text(json.dumps(value,indent=2)+'\n')
        with self.assertRaisesRegex(ValueError,'changed evidence artifact'):
            acceptance.verify_evidence_manifest(manifest,'source','42','head')

    def test_e0_rejects_regenerated_bundle_after_mandatory_reports_are_removed(self):
        bundle = self.root / 'evidence-bundle'
        report_inventory = acceptance.expected_report_inventory()
        for relative in report_inventory:
            path = bundle / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text('{}\n')
        for relative in report_inventory:
            (bundle / relative).unlink()

        manifest = create_manifest.create_manifest(bundle, 'source', '42', 'head')
        self.assertEqual(manifest['reportInventory'], [])
        with self.assertRaisesRegex(ValueError,'mandatory report inventory'):
            acceptance.verify_evidence_manifest(bundle/'manifest.json','source','42','head')

    def test_rejects_changed_theme_contract_and_missing_theme_execution(self):
        index=json.loads(acceptance.THEME_INDEX.read_text())
        changed=self.write('theme-index.json',index[:-1])
        with self.assertRaisesRegex(ValueError,'theme case index hash mismatch'):
            acceptance.verify_theme_report(changed,self.write('theme-report.json',{}),'source')
        expected=[item['expectedPi'] for item in index]
        report=dict(piSha=acceptance.PI_SHA,piSharpSha='source',pi=expected,
                    pisharp=json.loads(json.dumps(expected)),differences=[],match=True)
        report['pi']=[item for item in report['pi'] if item['id']!='settings-theme-user']
        with self.assertRaisesRegex(ValueError,'missing or skipped frozen theme case'):
            acceptance.verify_theme_report(acceptance.THEME_INDEX,self.write('theme-report.json',report),'source')
        report['pi']=expected; report['piSharpSha']='stale'
        with self.assertRaisesRegex(ValueError,'stale PiSharp source'):
            acceptance.verify_theme_report(acceptance.THEME_INDEX,self.write('theme-report.json',report),'source')

    def test_rejects_wrong_glob_source_matcher_and_aggregate(self):
        cases=json.loads(acceptance.GLOB_INDEX.read_text())
        import hashlib
        report=dict(piSha=acceptance.PI_SHA,piSharpSha='source',
            caseIndexSha256=hashlib.sha256(acceptance.GLOB_INDEX.read_bytes()).hexdigest(),
            required=len(cases),passed=len(cases),failed=0,skipped=0,
            cases=[dict(**case,piActual=case['expectedPi'],piSharpActual=case['expectedPi'],status='passed')
                   for case in cases])
        path=self.write('glob-report.json',report)
        report['piSharpSha']='wrong'
        with self.assertRaisesRegex(ValueError,'wrong PiSharp SHA'):
            acceptance.verify_glob_report(acceptance.GLOB_INDEX,self.write('glob-report.json',report),'source')
        report['piSharpSha']='source'; report['cases'][0]['piSharpActual']=not report['cases'][0]['expectedPi']
        report['cases'][0]['status']='failed'
        with self.assertRaisesRegex(ValueError,'PiSharp matcher mismatch'):
            acceptance.verify_glob_report(acceptance.GLOB_INDEX,self.write('glob-report.json',report),'source')
        report['cases'][0]['piSharpActual']=report['cases'][0]['expectedPi']; report['cases'][0]['status']='passed'
        report['passed']-=1
        with self.assertRaisesRegex(ValueError,'aggregate counts'):
            acceptance.verify_glob_report(acceptance.GLOB_INDEX,self.write('glob-report.json',report),'source')
        changed=json.loads(acceptance.GLOB_INDEX.read_text()); changed[0]['path']='changed'
        changed_path=self.write('changed-cases.json',changed)
        with self.assertRaisesRegex(ValueError,'glob index hash mismatch'):
            acceptance.verify_glob_report(changed_path,path,'source')

    def test_rejects_selection_inputs_skips_and_wrong_result(self):
        cases=json.loads(acceptance.SELECTION_INDEX.read_text())
        import hashlib
        report=dict(piSha=acceptance.PI_SHA,piSharpSha='source',
            selectionCaseIndexSha256=hashlib.sha256(acceptance.SELECTION_INDEX.read_bytes()).hexdigest(),
            scenarios=[case['input'] for case in cases],
            pi=[case['expectedPi'] for case in cases],
            pisharp=json.loads(json.dumps([case['expectedPi'] for case in cases])),
            differences=[],match=True)
        original=json.loads(json.dumps(report))
        path=self.write('selection-original-report.json',original)
        report['piSharpSha']='stale'
        with self.assertRaisesRegex(ValueError,'stale PiSharp source'):
            acceptance.verify_selection_report(acceptance.SELECTION_INDEX,
                self.write('selection-report.json',report),'source')
        report=json.loads(json.dumps(original))
        report['pisharp'].pop()
        with self.assertRaisesRegex(ValueError,'missing, duplicate, skipped'):
            acceptance.verify_selection_report(acceptance.SELECTION_INDEX,
                self.write('selection-report.json',report),'source')
        report=json.loads(json.dumps(original)); report['pisharp'][0]['themes']=[{'name':'wrong'}]
        with self.assertRaisesRegex(ValueError,'PiSharp selection differs'):
            acceptance.verify_selection_report(acceptance.SELECTION_INDEX,
                self.write('selection-report.json',report),'source')
        changed=json.loads(acceptance.SELECTION_INDEX.read_text()); changed[0]['input']['id']='changed'
        with self.assertRaisesRegex(ValueError,'selection case index hash mismatch'):
            acceptance.verify_selection_report(self.write('selection-index.json',changed),path,'source')

    def test_rejects_explicit_path_skip_or_tampered_result(self):
        cases=json.loads(acceptance.PATH_INDEX.read_text())
        import hashlib
        values=[case['expectedPi'] for case in cases]
        report=dict(piSha=acceptance.PI_SHA,piSharpSha='source',
            caseIndexSha256=hashlib.sha256(acceptance.PATH_INDEX.read_bytes()).hexdigest(),
            scenarios=[case['input'] for case in cases],pi=values,pisharp=values,differences=[],match=True,
            cases=[dict(id=case['id'],input=case['input'],expectedPi=case['expectedPi'],
                piActual=case['expectedPi'],piSharpActual=case['expectedPi'],status='passed') for case in cases])
        original=json.loads(json.dumps(report))
        report['cases'].pop()
        with self.assertRaisesRegex(ValueError,'missing, duplicate, skipped'):
            acceptance.verify_path_report(acceptance.PATH_INDEX,self.write('path-report.json',report),'source')
        report=json.loads(json.dumps(original)); report['piSharpSha']='stale'
        with self.assertRaisesRegex(ValueError,'stale PiSharp source'):
            acceptance.verify_path_report(acceptance.PATH_INDEX,self.write('path-report.json',report),'source')
        report=json.loads(json.dumps(original)); report['cases'][0]['piSharpActual']={'id':'wrong'}
        with self.assertRaisesRegex(ValueError,'PiSharp explicit path selection differs'):
            acceptance.verify_path_report(acceptance.PATH_INDEX,self.write('path-report.json',report),'source')

    def test_rejects_file_url_error_skip_stale_source_or_wrong_behavior(self):
        cases=json.loads(acceptance.FILE_URL_ERROR_INDEX.read_text())
        import hashlib
        report=dict(piSha=acceptance.PI_SHA,piSharpSha='source',
            caseIndexSha256=hashlib.sha256(acceptance.FILE_URL_ERROR_INDEX.read_bytes()).hexdigest(),
            required=len(cases),passed=len(cases),failed=0,skipped=0,
            cases=[dict(id=case['id'],pattern=case['pattern'],expectedPiError=case['expectedPiError'],
                piActual={'error':case['expectedPiError']},
                piSharpActual={operation:dict(threw=True,message=case['expectedPiError']['message'])
                               for operation in ('glob','exact')},status='passed') for case in cases])
        original=json.loads(json.dumps(report))
        report['piSharpSha']='stale'
        with self.assertRaisesRegex(ValueError,'stale PiSharp source'):
            acceptance.verify_file_url_error_report(acceptance.FILE_URL_ERROR_INDEX,
                self.write('file-url-report.json',report),'source')
        report=json.loads(json.dumps(original)); report['cases'].pop()
        with self.assertRaisesRegex(ValueError,'missing, duplicate, skipped'):
            acceptance.verify_file_url_error_report(acceptance.FILE_URL_ERROR_INDEX,
                self.write('file-url-report.json',report),'source')
        report=json.loads(json.dumps(original)); report['cases'][0]['piSharpActual']['glob']['threw']=False
        with self.assertRaisesRegex(ValueError,'PiSharp glob file URL error differs'):
            acceptance.verify_file_url_error_report(acceptance.FILE_URL_ERROR_INDEX,
                self.write('file-url-report.json',report),'source')

    def test_rejects_stale_evidence_source_and_ci(self):
        artifact=self.root/'capture.json'; artifact.write_text('{}')
        import hashlib
        manifest=self.write('manifest.json',dict(sourceSha='stale',piSha=acceptance.PI_SHA,
            baseSha=acceptance.BASE_SHA,prHeadSha='head',ciRunId='42',files=[
            dict(path=artifact.name,sha256=hashlib.sha256(artifact.read_bytes()).hexdigest())]))
        with self.assertRaisesRegex(ValueError,'stale evidence source'):
            acceptance.verify_evidence_manifest(manifest,'source','42','head')
        manifest=self.write('manifest.json',dict(sourceSha='source',piSha=acceptance.PI_SHA,
            baseSha=acceptance.BASE_SHA,prHeadSha='head',ciRunId='41',files=[
            dict(path=artifact.name,sha256=hashlib.sha256(artifact.read_bytes()).hexdigest())]))
        with self.assertRaisesRegex(ValueError,'stale CI run'):
            acceptance.verify_evidence_manifest(manifest,'source','42','head')
        manifest=self.write('manifest.json',dict(sourceSha='source',piSha=acceptance.PI_SHA,
            baseSha=acceptance.BASE_SHA,prHeadSha='stale',ciRunId='42',files=[
            dict(path=artifact.name,sha256=hashlib.sha256(artifact.read_bytes()).hexdigest())]))
        with self.assertRaisesRegex(ValueError,'stale PR head'):
            acceptance.verify_evidence_manifest(manifest,'source','42','head')


if __name__ == '__main__':
    unittest.main(verbosity=2)
