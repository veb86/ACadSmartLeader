;; The LEADER node owns the landing point (group 10). Each nested LEADER_LINE
;; stores the arrowhead and any intermediate vertices separately.
(defun sl-test-geometry (entity arrow side label / data tag in-leader in-line lines vertices landing dogleg)
  (setq data (entget entity '("BobrovLeader"))
        lines 0)
  (foreach tag data
    (cond
      ((equal tag '(302 . "LEADER{")) (setq in-leader T))
      ((equal tag '(303 . "}")) (setq in-leader nil))
      ((equal tag '(304 . "LEADER_LINE{")) (setq in-line T lines (1+ lines)))
      ((equal tag '(305 . "}")) (setq in-line nil))
      ((and in-line (= (car tag) 10)) (setq vertices (cons (cdr tag) vertices)))
      ((and in-leader (= (car tag) 10)) (setq landing (cdr tag)))
      ((and in-leader (not in-line) (= (car tag) 11)) (setq dogleg (cdr tag)))))
  (if (and (equal (cdr (assoc 0 data)) "MULTILEADER")
           (assoc -3 data)
           (= lines 1)
           (= (length vertices) 1)
           (equal (car vertices) arrow 1e-8)
           landing
           (not (equal landing arrow 1e-8))
           dogleg
           (> (* side (car dogleg)) 0.0))
    (princ (strcat "\nSL_GEOMETRY_OK: " label))
    (princ (strcat "\nSL_GEOMETRY_FAILED: " label "; leader lines=" (itoa lines)
                   "; vertices=" (itoa (length vertices)))))
  (princ))
